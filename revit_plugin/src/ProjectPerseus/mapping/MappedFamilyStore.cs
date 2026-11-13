using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using ProjectPerseus.federator;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Which consultant element each of our mapped families stands for: plugin data (Extensible Storage) on our family
    /// instance itself. v2 (Link Sync) = link instance uid + consultant uid fields + models/MappedFamilyData as JSON (rule,
    /// who / when, the sync baseline - consultant frame + type and our frame at the last place / accept - and rejected
    /// changes); v1 (fixed string fields, no baseline) is still read and is replaced by v2 on the next write. Follows
    /// replacements: Current() gives the element that replaced a deleted one (federator/ReplacementLinks). API context.
    /// </summary>
    internal static class MappedFamilyStore
    {
        private static readonly Guid SchemaV1 = new Guid("8D2B6F41-3C7E-4A95-B1D8-6E0F2A9C5B73");
        private static readonly Guid SchemaV2 = new Guid("5E7C2A19-8B4D-4F36-9A0E-3D1B7C6F2A84");
        private const string FLink = "LinkInstanceUid", FSource = "SourceUid", FSourceId = "SourceId", FRule = "RuleId",
                             FBy = "PlacedBy", FUtc = "PlacedUtc", FHow = "How", FJson = "Json";
        internal const string Placed = "placed", Linked = "linked";

        internal class Record
        {
            internal ElementId Element;
            internal MappedFamilyData Data = new MappedFamilyData();
            internal string LinkInstanceUid => Data.LinkInstanceUid;
            internal string SourceUid => Data.SourceUid;
            internal string SourceId => Data.SourceId;
            internal string RuleId => Data.RuleId;
            internal string By => Data.By;
            internal string How => Data.How;
            internal DateTime Utc => Data.Utc;
        }

        private static Schema V1(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaV1, "PerseusMappedFamily", "Project Perseus: the consultant element this family stands for.", b =>
                {
                    foreach (var f in new[] { FLink, FSource, FSourceId, FRule, FBy, FUtc, FHow }) b.AddSimpleField(f, typeof(string));
                })
            : Schema.Lookup(SchemaV1);

        private static Schema V2(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaV2, "PerseusMappedFamily2", "Project Perseus: the consultant element this family stands for + Link Sync state.", b =>
                {
                    foreach (var f in new[] { FLink, FSource, FJson }) b.AddSimpleField(f, typeof(string));
                })
            : Schema.Lookup(SchemaV2);

        /// <summary>Records on family instances of this model (optionally of one link instance); v2 wins over v1.</summary>
        internal static List<Record> All(Document doc, string linkInstanceUid = null)
        {
            var list = new List<Record>();
            var v1 = V1(false);
            var v2 = V2(false);
            var seen = new HashSet<ElementId>();
            foreach (var schema in new[] { v2, v1 })
            {
                if (schema == null) continue;
                foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new ExtensibleStorageFilter(schema.GUID)))
                {
                    if (seen.Contains(el.Id)) continue;
                    var r = Read(el, schema);
                    if (r == null) continue;
                    seen.Add(el.Id);
                    if (linkInstanceUid == null || r.LinkInstanceUid == linkInstanceUid) list.Add(r);
                }
            }
            return list;
        }

        internal static Record Get(Element el)
        {
            if (el == null) return null;
            var v2 = V2(false);
            var r = v2 == null ? null : Read(el, v2);
            if (r != null) return r;
            var v1 = V1(false);
            return v1 == null ? null : Read(el, v1);
        }

        private static Record Read(Element el, Schema schema)
        {
            Entity e;
            try { e = el.GetEntity(schema); } catch { return null; }
            if (e == null || !e.IsValid()) return null;
            if (schema.GUID == SchemaV2)
            {
                MappedFamilyData d = null;
                try { d = JsonConvert.DeserializeObject<MappedFamilyData>(e.Get<string>(FJson) ?? ""); } catch { }
                d = d ?? new MappedFamilyData();
                d.LinkInstanceUid = d.LinkInstanceUid ?? e.Get<string>(FLink);
                d.SourceUid = d.SourceUid ?? e.Get<string>(FSource);
                return new Record { Element = el.Id, Data = d };
            }
            DateTime.TryParse(e.Get<string>(FUtc), null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc);
            return new Record
            {
                Element = el.Id,
                Data = new MappedFamilyData
                {
                    LinkInstanceUid = e.Get<string>(FLink), SourceUid = e.Get<string>(FSource), SourceId = e.Get<string>(FSourceId),
                    RuleId = e.Get<string>(FRule), By = e.Get<string>(FBy), How = e.Get<string>(FHow), Utc = utc,
                },
            };
        }

        /// <summary>Writes the record as v2 (inside the caller's transaction), dropping a v1 one.</summary>
        internal static void Write(Element el, MappedFamilyData d)
        {
            var e = new Entity(V2(true));
            e.Set(FLink, d.LinkInstanceUid ?? "");
            e.Set(FSource, d.SourceUid ?? "");
            e.Set(FJson, JsonConvert.SerializeObject(d));
            el.SetEntity(e);
            var v1 = V1(false);
            if (v1 != null)
            {
                try { if (el.GetEntity(v1)?.IsValid() == true) el.DeleteEntity(v1); } catch { }
            }
        }

        /// <summary>Writes a fresh record (placed / linked now) with the sync baseline.</summary>
        internal static MappedFamilyData Write(Element el, string linkInstanceUid, string sourceUid, long sourceId, string ruleId, string by, string how,
                                               string sourceTypeKey = null, Frame sourceFrame = null, Frame oursFrame = null)
        {
            var d = new MappedFamilyData
            {
                LinkInstanceUid = linkInstanceUid, SourceUid = sourceUid, SourceId = sourceId.ToString(), RuleId = ruleId, By = by,
                Utc = DateTime.UtcNow, How = how ?? Placed, SourceTypeKey = sourceTypeKey,
                SourceFrame = MappingMath.Pack(sourceFrame), OursFrame = MappingMath.Pack(oursFrame),
            };
            Write(el, d);
            return d;
        }

        internal static void Clear(Element el)
        {
            foreach (var s in new[] { V1(false), V2(false) })
                if (s != null) { try { el.DeleteEntity(s); } catch { } }
        }

        /// <summary>The consultant element a record stands for now, following replacement links (old uid -> current uid).</summary>
        internal static string Current(Record r, IDictionary<string, string> replacementMap) =>
            r?.SourceUid != null && replacementMap != null && replacementMap.TryGetValue(r.SourceUid, out var now) ? now : r?.SourceUid;

        /// <summary>
        /// Rewrites records whose consultant element was replaced (replacementMap: old uid -> current uid). Records on
        /// families checked out by others are left (Current() still follows the link). Returns (updated, skipped).
        /// </summary>
        internal static Tuple<int, int> Repoint(Document doc, string linkInstanceUid, IDictionary<string, string> replacementMap,
                                                Func<string, long> idOf, string user)
        {
            int updated = 0, skipped = 0;
            var todo = All(doc, linkInstanceUid).Where(r => r.SourceUid != null && replacementMap.ContainsKey(r.SourceUid)).ToList();
            if (todo.Count == 0) return Tuple.Create(0, 0);
            using (var tx = new Transaction(doc, "Perseus: follow consultant replacements"))
            {
                tx.Start();
                foreach (var r in todo)
                {
                    if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, r.Element, out _) == CheckoutStatus.OwnedByOtherUser) { skipped++; continue; }
                    var el = doc.GetElement(r.Element);
                    if (el == null) continue;
                    string now = replacementMap[r.SourceUid];
                    r.Data.SourceUid = now;
                    r.Data.SourceId = idOf(now).ToString();
                    r.Data.By = user;
                    Write(el, r.Data);
                    updated++;
                }
                tx.Commit();
            }
            Log.Info($"[Mapping] repointed {updated} mapped families to replacement elements ({skipped} checked out by others)");
            return Tuple.Create(updated, skipped);
        }
    }
}
