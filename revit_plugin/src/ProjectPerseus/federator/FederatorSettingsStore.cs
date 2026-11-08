using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using ProjectPerseus.logging;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Shared federator settings (e.g. bake target views per link) in the host model as plugin data. Append-only like
    /// the review events: each save adds a record and the newest one per key wins, so a save never needs an element
    /// another user owns; the saver's own older records for the key are deleted when possible. API context, no open
    /// transaction.
    /// </summary>
    internal static class FederatorSettingsStore
    {
        private static readonly Guid SchemaGuid = new Guid("3E9C2F7A-6B1D-4C8E-A5F0-2D7B9E41C6A8");
        private const string FKey = "SettingsKey", FJson = "SettingsJson", FAuthor = "Author", FUtc = "SavedUtc";

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusFederatorSettings", "Project Perseus federator: shared settings.", b =>
                {
                    b.AddSimpleField(FKey, typeof(string));
                    b.AddSimpleField(FJson, typeof(string));
                    b.AddSimpleField(FAuthor, typeof(string));
                    b.AddSimpleField(FUtc, typeof(string));
                })
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        internal static T Load<T>(Document doc, string key) where T : class
        {
            var latest = Records(doc, key).OrderByDescending(r => r.Utc).FirstOrDefault();
            if (latest == null) return null;
            try { return JsonConvert.DeserializeObject<T>(latest.Json); }
            catch (Exception ex) { Log.Info($"[Federator] settings '{key}' unreadable: {ex.Message}"); return null; }
        }

        internal static void Save(Document doc, string key, object value, string author)
        {
            var schema = Schema(true);
            var old = Records(doc, key).Where(r => string.Equals(r.Author, author, StringComparison.OrdinalIgnoreCase)).ToList();
            using (var tx = new Transaction(doc, "Perseus: federator settings"))
            {
                tx.Start();
                var ds = DataStorage.Create(doc);
                var e = new Entity(schema);
                e.Set(FKey, key);
                e.Set(FJson, JsonConvert.SerializeObject(value));
                e.Set(FAuthor, author ?? "");
                e.Set(FUtc, DateTime.UtcNow.ToString("o"));
                ds.SetEntity(e);
                foreach (var r in old)
                {
                    if (doc.IsWorkshared &&
                        WorksharingUtils.GetCheckoutStatus(doc, r.Storage.Id, out _) == CheckoutStatus.OwnedByOtherUser) continue;
                    try { doc.Delete(r.Storage.Id); } catch { /* keep it: newest still wins */ }
                }
                tx.Commit();
            }
        }

        private class Record
        {
            internal DataStorage Storage;
            internal string Json, Author;
            internal DateTime Utc;
        }

        private static IEnumerable<Record> Records(Document doc, string key)
        {
            foreach (var kv in PluginDataStorage.All(doc, Schema(false)))
            {
                if (!string.Equals(kv.Value.Get<string>(FKey), key, StringComparison.Ordinal)) continue;
                DateTime.TryParse(kv.Value.Get<string>(FUtc), null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc);
                yield return new Record { Storage = kv.Key, Json = kv.Value.Get<string>(FJson), Author = kv.Value.Get<string>(FAuthor), Utc = utc };
            }
        }
    }
}
