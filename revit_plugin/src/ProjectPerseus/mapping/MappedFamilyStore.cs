using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using ProjectPerseus.federator;
using ProjectPerseus.logging;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Which consultant element each of our mapped families stands for: plugin data (Extensible Storage) on our family
    /// instance itself = link instance UniqueId + consultant element UniqueId / Id + mapping rule. Written when Perseus
    /// places the family (it's ours, new) or links an existing one. Follows replacements: when the consultant element
    /// was deleted and linked to a replacement (federator/ReplacementLinks), Current() gives the new element and
    /// Repoint() rewrites the record. API context.
    /// </summary>
    internal static class MappedFamilyStore
    {
        private static readonly Guid SchemaGuid = new Guid("8D2B6F41-3C7E-4A95-B1D8-6E0F2A9C5B73");
        private const string FLink = "LinkInstanceUid", FSource = "SourceUid", FSourceId = "SourceId", FRule = "RuleId",
                             FBy = "PlacedBy", FUtc = "PlacedUtc", FHow = "How";
        internal const string Placed = "placed", Linked = "linked";

        internal class Record
        {
            internal ElementId Element;
            internal string LinkInstanceUid, SourceUid, SourceId, RuleId, By, How;
            internal DateTime Utc;
        }

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusMappedFamily", "Project Perseus: the consultant element this family stands for.", b =>
                {
                    foreach (var f in new[] { FLink, FSource, FSourceId, FRule, FBy, FUtc, FHow }) b.AddSimpleField(f, typeof(string));
                })
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        /// <summary>Records on family instances of this model (optionally of one link instance).</summary>
        internal static List<Record> All(Document doc, string linkInstanceUid = null)
        {
            var list = new List<Record>();
            var schema = Schema(false);
            if (schema == null) return list;
            foreach (Element el in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
            {
                var r = Read(el, schema);
                if (r != null && (linkInstanceUid == null || r.LinkInstanceUid == linkInstanceUid)) list.Add(r);
            }
            return list;
        }

        internal static Record Get(Element el)
        {
            var schema = Schema(false);
            return schema == null || el == null ? null : Read(el, schema);
        }

        private static Record Read(Element el, Schema schema)
        {
            Entity e;
            try { e = el.GetEntity(schema); } catch { return null; }
            if (e == null || !e.IsValid()) return null;
            DateTime.TryParse(e.Get<string>(FUtc), null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc);
            return new Record
            {
                Element = el.Id, LinkInstanceUid = e.Get<string>(FLink), SourceUid = e.Get<string>(FSource), SourceId = e.Get<string>(FSourceId),
                RuleId = e.Get<string>(FRule), By = e.Get<string>(FBy), How = e.Get<string>(FHow), Utc = utc,
            };
        }

        /// <summary>Writes the record (inside the caller's transaction).</summary>
        internal static void Write(Element el, string linkInstanceUid, string sourceUid, long sourceId, string ruleId, string by, string how)
        {
            var e = new Entity(Schema(true));
            e.Set(FLink, linkInstanceUid ?? "");
            e.Set(FSource, sourceUid ?? "");
            e.Set(FSourceId, sourceId.ToString());
            e.Set(FRule, ruleId ?? "");
            e.Set(FBy, by ?? "");
            e.Set(FUtc, DateTime.UtcNow.ToString("o"));
            e.Set(FHow, how ?? Placed);
            el.SetEntity(e);
        }

        internal static void Clear(Element el)
        {
            var schema = Schema(false);
            if (schema != null) el.DeleteEntity(schema);
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
                    Write(el, r.LinkInstanceUid, now, idOf(now), r.RuleId, user, r.How);
                    updated++;
                }
                tx.Commit();
            }
            Log.Info($"[Mapping] repointed {updated} mapped families to replacement elements ({skipped} checked out by others)");
            return Tuple.Create(updated, skipped);
        }
    }
}
