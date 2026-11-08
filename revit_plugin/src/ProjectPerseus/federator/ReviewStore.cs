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
    /// Review comments / status changes saved in the host model as plugin data: one DataStorage per
    /// models/ReviewEvent, append-only (see ReviewLog for how they fold into a status + thread). A new event is
    /// owned by its author until they sync, which Revit then releases, so users never block each other. All calls
    /// need a Revit API context with no open transaction.
    /// </summary>
    internal static class ReviewStore
    {
        private static readonly Guid SchemaGuid = new Guid("d15c8f64-a95a-4ea4-a172-1bb714dbfb9c");
        private const int Format = 1;
        private const string FFormat = "Format", FId = "EventId", FComparison = "ComparisonId",
                             FElement = "LinkedElementUid", FJson = "EventJson";

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusFederatorReviewEvent",
                "Project Perseus federator: a review comment or status on a linked element.", b =>
                {
                    b.AddSimpleField(FFormat, typeof(int));
                    b.AddSimpleField(FId, typeof(string));
                    b.AddSimpleField(FComparison, typeof(string));
                    b.AddSimpleField(FElement, typeof(string));
                    b.AddSimpleField(FJson, typeof(string));
                })
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        /// <summary>Adds events in one transaction (e.g. one status applied to a whole selection).</summary>
        internal static void Add(Document doc, IList<ReviewEvent> events)
        {
            if (events == null || events.Count == 0) return;
            var schema = Schema(true);
            using (var tx = new Transaction(doc, events.Count == 1 ? "Perseus: review comment" : $"Perseus: review {events.Count} elements"))
            {
                tx.Start();
                foreach (var ev in events)
                {
                    if (string.IsNullOrEmpty(ev.Id)) ev.Id = Guid.NewGuid().ToString("N");
                    if (ev.CreatedUtc == default(DateTime)) ev.CreatedUtc = DateTime.UtcNow;
                    var ds = DataStorage.Create(doc);
                    var e = new Entity(schema);
                    e.Set(FFormat, Format);
                    e.Set(FId, ev.Id);
                    e.Set(FComparison, ev.ComparisonId ?? "");
                    e.Set(FElement, ev.LinkedElementUid ?? "");
                    e.Set(FJson, JsonConvert.SerializeObject(ev, Json));
                    ds.SetEntity(e);
                    ev.StorageId = ds.Id.GetIdValue();
                }
                tx.Commit();
            }
        }

        internal static List<ReviewEvent> Load(Document doc)
        {
            var list = new List<ReviewEvent>();
            foreach (var kv in PluginDataStorage.All(doc, Schema(false)))
            {
                try
                {
                    var ev = JsonConvert.DeserializeObject<ReviewEvent>(kv.Value.Get<string>(FJson));
                    if (ev == null) continue;
                    ev.StorageId = kv.Key.Id.GetIdValue();
                    list.Add(ev);
                }
                catch (Exception ex) { Log.Info($"[Federator] unreadable review event in DataStorage {kv.Key.Id.GetIdValue()}: {ex.Message}"); }
            }
            return list;
        }

        /// <summary>Deletes one of the user's own events (e.g. a comment posted by mistake).</summary>
        internal static bool Delete(Document doc, string eventId, string user, out string reason)
        {
            var kv = PluginDataStorage.All(doc, Schema(false))
                .FirstOrDefault(x => string.Equals(x.Value.Get<string>(FId), eventId, StringComparison.Ordinal));
            if (kv.Key == null) { reason = "It's no longer in the model."; return false; }
            var ev = JsonConvert.DeserializeObject<ReviewEvent>(kv.Value.Get<string>(FJson));
            if (!PluginDataStorage.CanDelete(doc, kv.Key, ev?.Author, user, out reason)) return false;
            using (var tx = new Transaction(doc, "Perseus: delete review comment"))
            {
                tx.Start();
                doc.Delete(kv.Key.Id);
                tx.Commit();
            }
            return true;
        }
    }
}
