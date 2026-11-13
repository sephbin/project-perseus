using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using ProjectPerseus.federator;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Link Sync decisions, comments and assignments (models/LinkSyncEvent) as plugin data in the host model: one
    /// DataStorage per batch of events, append-only (like federator/ReviewStore), so nobody needs to own our family to
    /// comment on it or assign it; a new record is owned by its author until they sync. Folded by mapping/LinkSyncNotes.
    /// </summary>
    internal static class LinkSyncLog
    {
        private static readonly Guid SchemaGuid = new Guid("3F9A6C2E-71B4-4D8A-9E05-B2C7D1E4F6A3");
        private const string FJson = "EventsJson";
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusLinkSyncEvents", "Project Perseus Link Sync: decisions, comments and assignments.",
                b => b.AddSimpleField(FJson, typeof(string)))
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        /// <summary>Adds events: inside the caller's open transaction when <paramref name="inTransaction"/>, else in its own.</summary>
        internal static void Add(Document doc, IList<LinkSyncEvent> events, bool inTransaction = false)
        {
            if (events == null || events.Count == 0) return;
            foreach (var e in events)
            {
                if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N");
                if (e.Utc == default(DateTime)) e.Utc = DateTime.UtcNow;
            }
            void Write()
            {
                var ds = DataStorage.Create(doc);
                var entity = new Entity(Schema(true));
                entity.Set(FJson, JsonConvert.SerializeObject(events, Json));
                ds.SetEntity(entity);
            }
            if (inTransaction) { Write(); return; }
            using (var tx = new Transaction(doc, events.Count == 1 ? "Perseus: Link Sync note" : $"Perseus: Link Sync notes ({events.Count})"))
            {
                tx.Start();
                Write();
                tx.Commit();
            }
        }

        internal static List<LinkSyncEvent> Load(Document doc)
        {
            var list = new List<LinkSyncEvent>();
            var schema = Schema(false);
            if (schema == null) return list;
            foreach (var kv in PluginDataStorage.All(doc, schema))
            {
                try
                {
                    var batch = JsonConvert.DeserializeObject<List<LinkSyncEvent>>(kv.Value.Get<string>(FJson));
                    if (batch != null) list.AddRange(batch);
                }
                catch (Exception ex) { Log.Info($"[LinkSync] unreadable event record {kv.Key.Id}: {ex.Message}"); }
            }
            return list;
        }
    }
}
