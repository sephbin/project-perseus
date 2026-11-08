using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Shared helpers for the federator's Extensible Storage records (ComparisonStore, ReviewStore): each record is
    /// its own DataStorage element so records are never edited by more than one user.
    /// </summary>
    internal static class PluginDataStorage
    {
        internal static IEnumerable<KeyValuePair<DataStorage, Entity>> All(Document doc, Schema schema)
        {
            if (schema == null) yield break;
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                Entity entity;
                try { entity = ds.GetEntity(schema); } catch { continue; }
                if (entity != null && entity.IsValid()) yield return new KeyValuePair<DataStorage, Entity>(ds, entity);
            }
        }

        /// <summary>Whether this user may delete the record: they wrote it and nobody else has it checked out.</summary>
        internal static bool CanDelete(Document doc, Element record, string createdBy, string user, out string reason)
        {
            reason = null;
            if (!string.Equals(createdBy, user, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Only {createdBy} can delete this.";
                return false;
            }
            if (doc.IsWorkshared &&
                WorksharingUtils.GetCheckoutStatus(doc, record.Id, out string owner) == CheckoutStatus.OwnedByOtherUser)
            {
                reason = $"It's checked out by {owner}.";
                return false;
            }
            return true;
        }

        internal static Schema EnsureSchema(Guid guid, string name, string doc, Action<SchemaBuilder> fields)
        {
            var schema = Schema.Lookup(guid);
            if (schema != null) return schema;
            var b = new SchemaBuilder(guid);
            b.SetSchemaName(name);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.SetDocumentation(doc);
            fields(b);
            return b.Finish();
        }

        internal static byte[] Gzip(string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal))
                {
                    var bytes = Encoding.UTF8.GetBytes(text ?? "");
                    gz.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }
        }

        internal static string Gunzip(IList<byte> data)
        {
            if (data == null || data.Count == 0) return "";
            var arr = data as byte[] ?? new List<byte>(data).ToArray();
            using (var gz = new GZipStream(new MemoryStream(arr), CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}
