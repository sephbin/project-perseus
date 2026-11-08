using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Link comparisons saved in the host model as plugin data: one DataStorage per comparison, written once and
    /// never edited. Header = ComparisonRecord JSON (listing is cheap); changes = gzip JSON in a byte array, read only
    /// when a comparison is opened. Schemas can't change once created, so new fields go into the JSON (Format bumps
    /// if the shape ever changes incompatibly). All calls need a Revit API context; Save/Delete open their own
    /// transaction, so no transaction may be open.
    /// </summary>
    internal static class ComparisonStore
    {
        private static readonly Guid SchemaGuid = new Guid("68b864fb-0ead-417e-8882-bc5e2258167b");
        private const int Format = 1;
        private const string FFormat = "Format", FId = "RecordId", FLink = "LinkInstanceUid", FHeader = "HeaderJson", FChanges = "ChangesGzip";

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusFederatorComparison",
                "Project Perseus federator: comparison of a linked model against an earlier version.", b =>
                {
                    b.AddSimpleField(FFormat, typeof(int));
                    b.AddSimpleField(FId, typeof(string));
                    b.AddSimpleField(FLink, typeof(string));
                    b.AddSimpleField(FHeader, typeof(string));
                    b.AddArrayField(FChanges, typeof(byte));
                })
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        internal static void Save(Document doc, ComparisonRecord rec)
        {
            if (string.IsNullOrEmpty(rec.Id)) rec.Id = Guid.NewGuid().ToString("N");
            byte[] changes = PluginDataStorage.Gzip(JsonConvert.SerializeObject(rec.Changes ?? new List<ElementChange>(), Json));
            var schema = Schema(true);
            using (var tx = new Transaction(doc, "Perseus: save link comparison"))
            {
                tx.Start();
                var ds = DataStorage.Create(doc);
                var e = new Entity(schema);
                e.Set(FFormat, Format);
                e.Set(FId, rec.Id);
                e.Set(FLink, rec.LinkInstanceUid ?? "");
                e.Set(FHeader, JsonConvert.SerializeObject(rec, Json));
                e.Set<IList<byte>>(FChanges, changes.ToList());
                ds.SetEntity(e);
                tx.Commit();
                rec.StorageId = ds.Id.GetIdValue();
            }
            Log.Info($"[Federator] saved comparison {rec.Id} of '{rec.LinkTitle}' ({rec.Added} added, {rec.Deleted} deleted, " +
                     $"{rec.Modified} modified; {changes.Length / 1024} KB)");
        }

        /// <summary>Headers of every comparison in the model (Changes not loaded), newest first.</summary>
        internal static List<ComparisonRecord> List(Document doc)
        {
            var list = new List<ComparisonRecord>();
            foreach (var kv in PluginDataStorage.All(doc, Schema(false)))
            {
                try
                {
                    var rec = JsonConvert.DeserializeObject<ComparisonRecord>(kv.Value.Get<string>(FHeader));
                    if (rec == null) continue;
                    rec.StorageId = kv.Key.Id.GetIdValue();
                    list.Add(rec);
                }
                catch (Exception ex) { Log.Info($"[Federator] unreadable comparison in DataStorage {kv.Key.Id.GetIdValue()}: {ex.Message}"); }
            }
            return list.OrderByDescending(r => r.CreatedUtc).ToList();
        }

        /// <summary>The changes of one comparison, or null if it isn't in the model.</summary>
        internal static List<ElementChange> LoadChanges(Document doc, string recordId)
        {
            var kv = Find(doc, recordId);
            if (kv.Key == null) return null;
            string json = PluginDataStorage.Gunzip(kv.Value.Get<IList<byte>>(FChanges));
            return JsonConvert.DeserializeObject<List<ElementChange>>(json) ?? new List<ElementChange>();
        }

        /// <summary>Deletes a comparison its author no longer wants. Review events on it are kept (they belong to
        /// whoever wrote them) and simply stop being shown.</summary>
        internal static bool Delete(Document doc, string recordId, string user, out string reason)
        {
            var kv = Find(doc, recordId);
            if (kv.Key == null) { reason = "It's no longer in the model."; return false; }
            var rec = JsonConvert.DeserializeObject<ComparisonRecord>(kv.Value.Get<string>(FHeader));
            if (!PluginDataStorage.CanDelete(doc, kv.Key, rec?.CreatedBy, user, out reason)) return false;
            using (var tx = new Transaction(doc, "Perseus: delete link comparison"))
            {
                tx.Start();
                doc.Delete(kv.Key.Id);
                tx.Commit();
            }
            return true;
        }

        private static KeyValuePair<DataStorage, Entity> Find(Document doc, string recordId) =>
            PluginDataStorage.All(doc, Schema(false))
                .FirstOrDefault(kv => string.Equals(kv.Value.Get<string>(FId), recordId, StringComparison.Ordinal));
    }
}
