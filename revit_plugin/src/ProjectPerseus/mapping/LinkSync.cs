using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.federator;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Link Sync, the Revit side: reads a link + our tracked families into a mapping/LinkSyncPlanner input (Collect),
    /// applies the user's approve / reject decisions (Apply, one undo step) and writes the Perseus_LinkSource /
    /// Perseus_LinkStatus shared parameters on our mapped families. API context. UI: ui/LinkSyncWindow.
    /// </summary>
    internal static class LinkSync
    {
        internal class LinkInfo
        {
            internal string Uid, Name, ModelKey;
            internal RevitLinkInstance Instance;
            internal FamilyMapping Mapping;
            public override string ToString() => Name;
        }

        internal class Decision
        {
            internal SyncItem Item;
            internal bool Approve;
        }

        internal class ApplyResult
        {
            internal int Approved, Rejected, Skipped, Failed, Replaced;
            internal List<string> Warnings = new List<string>();
            internal ElementId Last;
        }

        private static string RejectedKey(string linkUid) => "linksync-rejected:" + linkUid;

        /// <summary>Loaded links with their mapping (Mapped = has rules).</summary>
        internal static List<LinkInfo> Links(Document doc)
        {
            var list = new List<LinkInfo>();
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                var linkDoc = inst.GetLinkDocument();
                if (linkDoc == null) continue;
                string name = doc.GetElement(inst.GetTypeId())?.Name ?? inst.Name;
                string key = VersionCatalog.ModelKey(doc, inst, linkDoc, name);
                list.Add(new LinkInfo { Uid = inst.UniqueId, Name = name, ModelKey = key, Instance = inst, Mapping = FamilyMapStore.Load(doc, key) });
            }
            foreach (var g in list.GroupBy(l => l.Name).Where(g => g.Count() > 1)) { int i = 1; foreach (var l in g) l.Name += $"  #{i++}"; }
            return list.OrderBy(l => l.Name).ToList();
        }

        /// <summary>Everything the planner needs for one link (records of all links read once by the caller: allRecords).</summary>
        internal static List<SyncItem> Collect(Document doc, LinkInfo link, List<ReviewEvent> events, List<MappedFamilyStore.Record> allRecords)
        {
            var inst = link.Instance;
            var linkDoc = inst?.GetLinkDocument();
            if (linkDoc == null) return new List<SyncItem>();
            var toHost = inst.GetTotalTransform();
            var input = new SyncInput
            {
                LinkUid = link.Uid,
                Rules = link.Mapping.Rules ?? new List<FamilyMapRule>(),
                HiddenCategories = new HashSet<string>(link.Mapping.HiddenCategories ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                ReplacementMap = ReplacementLinks.Map(ReplacementLinks.Fold(events, link.Uid)),
            };
            var rej = FederatorSettingsStore.Load<LinkSyncRejections>(doc, RejectedKey(link.Uid));
            if (rej?.Sources != null) foreach (var u in rej.Sources) input.RejectedNew.Add(u);

            var records = allRecords.Where(r => r.LinkInstanceUid == link.Uid).ToList();
            var needed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (r.SourceUid != null) needed.Add(r.SourceUid);
                if (r.SourceUid != null && input.ReplacementMap.TryGetValue(r.SourceUid, out var now)) needed.Add(now);
            }
            var ruleKeys = new HashSet<string>(input.Rules.Select(r => r.SourceKey), StringComparer.Ordinal);
            foreach (FamilyInstance fi in new FilteredElementCollector(linkDoc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                if (fi.SuperComponent != null || fi.Category == null || fi.Symbol == null) continue;
                string key = FamilyMapRule.Key(fi.Category.Name, fi.Symbol.FamilyName, fi.Symbol.Name);
                if (!ruleKeys.Contains(key) && !needed.Contains(fi.UniqueId)) continue;
                input.Sources.Add(new SyncSource { Uid = fi.UniqueId, Id = fi.Id.GetIdValue(), TypeKey = key, Category = fi.Category.Name, Frame = FamilyPlacer.SourceFrame(fi, toHost) });
            }
            foreach (var r in records)
            {
                if (!(doc.GetElement(r.Element) is FamilyInstance fi)) continue;
                input.Ours.Add(Ours(fi, r.Data));
            }
            var targetUids = new HashSet<string>(input.Rules.Where(r => !string.IsNullOrEmpty(r.TargetSymbolUid)).Select(r => r.TargetSymbolUid), StringComparer.Ordinal);
            var targetIds = new HashSet<ElementId>(targetUids.Select(u => doc.GetElement(u)?.Id).Where(id => id != null));
            if (targetIds.Count > 0)
            {
                var recorded = new HashSet<ElementId>(allRecords.Select(r => r.Element));
                foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
                    if (fi.Symbol != null && targetIds.Contains(fi.Symbol.Id) && !recorded.Contains(fi.Id)) input.Unrecorded.Add(Ours(fi, null));
            }
            return LinkSyncPlanner.Plan(input);
        }

        private static SyncOurs Ours(FamilyInstance fi, MappedFamilyData record) => new SyncOurs
        {
            Id = fi.Id.GetIdValue(), TypeUid = fi.Symbol?.UniqueId, TypeName = fi.Symbol == null ? "?" : $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}",
            Frame = FamilyPlacer.FrameOf(fi), Record = record,
        };

        private static int Order(SyncKind k)
        {
            switch (k)
            {
                case SyncKind.Replaced: return 0;
                case SyncKind.ConsultantType: return 1;
                case SyncKind.MappingType: return 2;
                case SyncKind.Moved: case SyncKind.KeptByHand: case SyncKind.KeptRejected: return 3;
                case SyncKind.Match: return 4;
                case SyncKind.New: return 5;
                case SyncKind.Orphaned: return 6;
                default: return 9;
            }
        }

        /// <summary>Applies approve / reject decisions (one undo step). Ours checked out by others are skipped.</summary>
        internal static ApplyResult Apply(Document doc, List<Decision> decisions, Dictionary<string, LinkInfo> links, string user,
                                          Action<string, double> progress = null)
        {
            var res = new ApplyResult();
            if (decisions.Count == 0) return res;
            // Parameters on the categories of our families involved (outside any transaction).
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in decisions)
            {
                if (d.Item.OursId != 0) { var c = doc.GetElement(RevitExtensions.CreateId(d.Item.OursId))?.Category?.Name; if (c != null) cats.Add(c); }
                if (d.Item.TargetUid != null) { var c = doc.GetElement(d.Item.TargetUid)?.Category?.Name; if (c != null) cats.Add(c); }
            }
            try { SharedParameterManager.EnsureLinkSyncParameters(doc, cats); }
            catch (Exception ex) { res.Warnings.Add("couldn't add the Perseus link parameters: " + ex.Message); Log.Warn($"[LinkSync] parameters: {ex.Message}"); }

            var idMap = new Dictionary<long, long>();
            var rejectedNew = new Dictionary<string, HashSet<string>>();
            var levels = FamilyPlacer.Levels(doc);
            var ordered = decisions.OrderBy(d => Order(d.Item.Kind)).ToList();
            using (var tg = new TransactionGroup(doc, "Perseus: Link Sync"))
            {
                tg.Start();
                using (var tx = new Transaction(doc, "Perseus: Link Sync"))
                {
                    tx.Start();
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new FamilyPlacer.Quiet());
                    tx.SetFailureHandlingOptions(fo);
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        if (i % 20 == 0) progress?.Invoke($"{i} of {ordered.Count}", (double)i / ordered.Count);
                        var d = ordered[i];
                        try
                        {
                            string outcome = One(doc, d, links, user, levels, idMap, rejectedNew, res);
                            if (outcome == "skipped") res.Skipped++;
                            else if (outcome != null) { res.Failed++; if (!res.Warnings.Contains(outcome)) res.Warnings.Add(outcome); }
                            else if (d.Approve) res.Approved++; else res.Rejected++;
                        }
                        catch (Exception ex)
                        {
                            res.Failed++;
                            if (!res.Warnings.Contains(ex.Message)) res.Warnings.Add(ex.Message);
                            Log.Info($"[LinkSync] {d.Item.Kind} {(d.Approve ? "approve" : "reject")} failed: {ex.Message}");
                        }
                    }
                    tx.Commit();
                }
                foreach (var kv in rejectedNew)
                {
                    var cur = FederatorSettingsStore.Load<LinkSyncRejections>(doc, RejectedKey(kv.Key)) ?? new LinkSyncRejections();
                    foreach (var u in kv.Value) if (!cur.Sources.Contains(u)) cur.Sources.Add(u);
                    FederatorSettingsStore.Save(doc, RejectedKey(kv.Key), cur, user);
                }
                tg.Assimilate();
            }
            Log.Info($"[LinkSync] applied: {res.Approved} approved, {res.Rejected} rejected, {res.Replaced} re-placed, {res.Skipped} checked out by others, {res.Failed} failed" +
                     (res.Warnings.Count > 0 ? " (" + string.Join("; ", res.Warnings.Take(3)) + ")" : ""));
            return res;
        }

        /// <summary>One decision; null = done, "skipped" = checked out by another user, else a warning.</summary>
        private static string One(Document doc, Decision d, Dictionary<string, LinkInfo> links, string user, List<Level> levels,
                                  Dictionary<long, long> idMap, Dictionary<string, HashSet<string>> rejectedNew, ApplyResult res)
        {
            var it = d.Item;
            if (!links.TryGetValue(it.LinkUid ?? "", out var link) || link.Instance?.GetLinkDocument() == null) return "the link isn't loaded";
            var inst = link.Instance;
            var linkDoc = inst.GetLinkDocument();
            var toHost = inst.GetTotalTransform();
            long oid = it.OursId;
            while (oid != 0 && idMap.TryGetValue(oid, out var moved)) oid = moved;
            var ours = oid != 0 ? doc.GetElement(RevitExtensions.CreateId(oid)) as FamilyInstance : null;
            if (oid != 0 && ours == null) return "ours is gone";
            if (ours != null && doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, ours.Id, out _) == CheckoutStatus.OwnedByOtherUser) return "skipped";
            var rec = ours != null ? MappedFamilyStore.Get(ours)?.Data : null;
            FamilyMapRule rule = link.Mapping.Rules.FirstOrDefault(r => r.Id == it.RuleId);
            Element source = it.SourceUid != null ? linkDoc.GetElement(it.SourceUid) : null;

            if (!d.Approve)
            {
                switch (it.Kind)
                {
                    case SyncKind.New:
                    case SyncKind.Match:
                        if (!rejectedNew.TryGetValue(link.Uid, out var set)) rejectedNew[link.Uid] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(it.SourceUid);
                        return null;
                    case SyncKind.Moved:
                    case SyncKind.KeptByHand:
                        if (rec == null) return "ours has no Perseus record";
                        rec.RejectedExpected = MappingMath.Pack(it.Expected);
                        break;
                    case SyncKind.ConsultantType: if (rec == null) return "ours has no Perseus record"; rec.RejectedSourceType = it.SourceTypeKey; break;
                    case SyncKind.MappingType: if (rec == null) return "ours has no Perseus record"; rec.RejectedTargetUid = it.TargetUid; break;
                    case SyncKind.Replaced: if (rec == null) return "ours has no Perseus record"; rec.RejectedReplacement = it.NewSourceUid; break;
                    case SyncKind.Orphaned: if (rec == null) return "ours has no Perseus record"; rec.RejectedGone = true; break;
                    default: return null;
                }
                MappedFamilyStore.Write(ours, rec);
                SetParameters(ours, link.Name, rec.SourceId, it.Kind == SyncKind.Orphaned ? "Kept: consultant deleted" : "Kept: change rejected");
                return null;
            }

            switch (it.Kind)
            {
                case SyncKind.Replaced:
                {
                    if (rec == null) return "ours has no Perseus record";
                    rec.SourceUid = it.NewSourceUid;
                    rec.SourceId = it.NewSourceId.ToString();
                    rec.RejectedReplacement = null;
                    rec.By = user;
                    MappedFamilyStore.Write(ours, rec);
                    SetParameters(ours, link.Name, rec.SourceId, null);
                    return null;
                }
                case SyncKind.ConsultantType:
                case SyncKind.MappingType:
                {
                    if (rec == null) return "ours has no Perseus record";
                    if (string.IsNullOrEmpty(it.TargetUid)) return $"{LinkSyncPlanner.Pretty(it.SourceTypeKey)} isn't mapped: map it in Family mapping first";
                    var sym = doc.GetElement(it.TargetUid) as FamilySymbol;
                    if (sym == null) return "our mapped type isn't in the model any more";
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    if (ours.Symbol.Id != sym.Id) ours.ChangeTypeId(sym.Id);
                    if (it.Kind == SyncKind.ConsultantType) { rec.SourceTypeKey = it.SourceTypeKey; rec.RuleId = it.RuleId ?? rec.RuleId; rec.RejectedSourceType = null; }
                    else rec.RejectedTargetUid = null;
                    MappedFamilyStore.Write(ours, rec);
                    SetParameters(ours, link.Name, rec.SourceId, null);
                    res.Last = ours.Id;
                    return null;
                }
                case SyncKind.Moved:
                case SyncKind.KeptByHand:
                case SyncKind.KeptRejected:
                {
                    if (source == null) return "the consultant element is gone";
                    rule = rule ?? link.Mapping.Rules.FirstOrDefault(r => r.Id == rec?.RuleId);
                    if (rule == null) return "no mapping rule for it";
                    var srcFrame = FamilyPlacer.SourceFrame(source, toHost);
                    var expected = MappingMath.Place(srcFrame, rule.OffsetMm, rule.RotationDeg);
                    var now = FamilyPlacer.MoveTo(doc, ours, expected, rule.ToleranceXyMm, rule.ToleranceZMm, inst, rule, source, levels, user, out string warning, out bool replaced);
                    if (now == null) return warning ?? "couldn't move it";
                    if (replaced) { idMap[oid] = now.Id.GetIdValue(); res.Replaced++; }
                    var data = MappedFamilyStore.Get(now)?.Data ?? rec ?? new MappedFamilyData { LinkInstanceUid = link.Uid, RuleId = rule.Id, How = MappedFamilyStore.Placed };
                    data.SourceUid = source.UniqueId; data.SourceId = source.Id.GetIdValue().ToString();
                    data.SourceTypeKey = FamilyPlacer.TypeKey(source);
                    data.SourceFrame = MappingMath.Pack(srcFrame);
                    data.OursFrame = MappingMath.Pack(FamilyPlacer.FrameOf(now));
                    data.RejectedExpected = null;
                    data.By = user; data.Utc = DateTime.UtcNow;
                    MappedFamilyStore.Write(now, data);
                    SetParameters(now, link.Name, data.SourceId, null);
                    res.Last = now.Id;
                    return null;
                }
                case SyncKind.Match:
                {
                    if (source == null) return "the consultant element is gone";
                    if (rule == null) return "no mapping rule for it";
                    var data = MappedFamilyStore.Write(ours, link.Uid, source.UniqueId, source.Id.GetIdValue(), rule.Id, user, MappedFamilyStore.Linked,
                                                       FamilyPlacer.TypeKey(source), FamilyPlacer.SourceFrame(source, toHost), FamilyPlacer.FrameOf(ours));
                    SetParameters(ours, link.Name, data.SourceId, null);
                    res.Last = ours.Id;
                    return null;
                }
                case SyncKind.New:
                {
                    if (source == null) return "the consultant element is gone";
                    if (rule == null) return "no mapping rule for it";
                    var sym = doc.GetElement(rule.TargetSymbolUid) as FamilySymbol;
                    if (sym == null) return "our mapped type isn't in the model any more";
                    var fi = FamilyPlacer.Place(doc, inst, rule, sym, source, levels, FamilyPlacer.WorksetByName(doc, rule.Workset), user, out string warning);
                    if (fi == null) return warning ?? "couldn't place it";
                    SetParameters(fi, link.Name, source.Id.GetIdValue().ToString(), null);
                    res.Last = fi.Id;
                    return null;
                }
                case SyncKind.Orphaned:
                    doc.Delete(ours.Id);
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>Perseus_LinkSource = "link · consultant id", Perseus_LinkStatus (null = In sync). Only writes changed values.</summary>
        private static void SetParameters(Element ours, string linkName, string sourceId, string status)
        {
            Set(ours, SharedParameterManager.LinkSourceName, $"{linkName} · {sourceId}");
            Set(ours, SharedParameterManager.LinkStatusName, status ?? LinkSyncPlanner.Status(SyncKind.InSync));
        }

        private static bool Set(Element el, string name, string value)
        {
            try
            {
                var p = el?.LookupParameter(name);
                if (p == null || p.IsReadOnly || p.StorageType != Autodesk.Revit.DB.StorageType.String) return false;
                if (p.AsString() == value) return false;
                p.Set(value ?? "");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Writes Perseus_LinkSource / Perseus_LinkStatus on every one of ours in the plan (status = the open changes, else
        /// In sync). Ours checked out by others are skipped; unchanged values aren't written. Returns (written, skipped).
        /// </summary>
        internal static Tuple<int, int> WriteStatus(Document doc, List<SyncItem> items, Dictionary<string, LinkInfo> links)
        {
            var byOurs = items.Where(i => i.OursId != 0 && i.Kind != SyncKind.Match).GroupBy(i => i.OursId).ToList();
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in byOurs) { var c = doc.GetElement(RevitExtensions.CreateId(g.Key))?.Category?.Name; if (c != null) cats.Add(c); }
            SharedParameterManager.EnsureLinkSyncParameters(doc, cats);
            int written = 0, skipped = 0;
            using (var tx = new Transaction(doc, "Perseus: Link Sync status parameters"))
            {
                tx.Start();
                foreach (var g in byOurs)
                {
                    var el = doc.GetElement(RevitExtensions.CreateId(g.Key));
                    if (el == null) continue;
                    var open = g.Where(i => i.Kind != SyncKind.InSync).Select(i => LinkSyncPlanner.Status(i.Kind)).Distinct().ToList();
                    string status = open.Count == 0 ? LinkSyncPlanner.Status(SyncKind.InSync) : string.Join("; ", open);
                    var first = g.First();
                    links.TryGetValue(first.LinkUid ?? "", out var link);
                    string source = $"{link?.Name ?? "?"} · {(first.Kind == SyncKind.Replaced ? first.SourceId : first.SourceId)}";
                    var p1 = el.LookupParameter(SharedParameterManager.LinkSourceName);
                    var p2 = el.LookupParameter(SharedParameterManager.LinkStatusName);
                    bool changes = (p1 != null && p1.AsString() != source) || (p2 != null && p2.AsString() != status);
                    if (!changes) continue;
                    if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, el.Id, out _) == CheckoutStatus.OwnedByOtherUser) { skipped++; continue; }
                    Set(el, SharedParameterManager.LinkSourceName, source);
                    Set(el, SharedParameterManager.LinkStatusName, status);
                    written++;
                }
                tx.Commit();
            }
            return Tuple.Create(written, skipped);
        }
    }
}
