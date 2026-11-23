using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using ProjectPerseus.federator;

namespace ProjectPerseus.naming
{
    /// <summary>
    /// Which name each Perseus parameter key has in THIS model ("perseus.id" -> "COX_Perseus_ID"), as plugin data: one
    /// DataStorage per record, append-only, newest per key wins (a write never needs an element another user owns).
    /// Written when Perseus creates a keyed parameter and when Rename parameters replaces one; read by ParameterNames so
    /// a model keeps using the parameters it has until someone asks for them to be renamed.
    /// </summary>
    internal static class ParameterNameRecords
    {
        private static readonly Guid SchemaGuid = new Guid("8D2A6C41-3F7E-4B95-9E1C-5A0B7D3F2E64");
        private const string FKey = "ParameterKey", FName = "ParameterName", FAuthor = "Author", FUtc = "SavedUtc";

        private static Schema Schema(bool create) => create
            ? PluginDataStorage.EnsureSchema(SchemaGuid, "PerseusParameterName",
                "Project Perseus: the name a Perseus parameter key has in this model.", b =>
                {
                    b.AddSimpleField(FKey, typeof(string));
                    b.AddSimpleField(FName, typeof(string));
                    b.AddSimpleField(FAuthor, typeof(string));
                    b.AddSimpleField(FUtc, typeof(string));
                })
            : Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid);

        /// <summary>Key -> name in use, newest record per key.</summary>
        internal static Dictionary<string, string> Load(Document doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var schema = Schema(false);
            if (schema == null) return result;
            var newest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in PluginDataStorage.All(doc, schema))
            {
                string key = kv.Value.Get<string>(FKey) ?? "", name = kv.Value.Get<string>(FName) ?? "";
                string utc = kv.Value.Get<string>(FUtc) ?? "";
                if (key.Length == 0 || name.Length == 0) continue;
                if (newest.TryGetValue(key, out var seen) && string.CompareOrdinal(seen, utc) >= 0) continue;
                newest[key] = utc;
                result[key] = name;
            }
            return result;
        }

        /// <summary>Adds records (key -> name). Must be called inside an open transaction.</summary>
        internal static void Add(Document doc, IEnumerable<KeyValuePair<string, string>> names, string author)
        {
            var list = names.Where(n => !string.IsNullOrEmpty(n.Key) && !string.IsNullOrEmpty(n.Value)).ToList();
            if (list.Count == 0) return;
            var schema = Schema(true);
            string utc = DateTime.UtcNow.ToString("o");
            foreach (var n in list)
            {
                var ds = DataStorage.Create(doc);
                var e = new Entity(schema);
                e.Set(FKey, n.Key);
                e.Set(FName, n.Value);
                e.Set(FAuthor, author ?? "");
                e.Set(FUtc, utc);
                ds.SetEntity(e);
            }
            ParameterNames.Invalidate(doc);
        }
    }
}
