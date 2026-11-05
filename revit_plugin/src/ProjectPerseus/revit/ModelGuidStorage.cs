using System;
using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using ProjectPerseus;

using ProjectPerseus.logging;
namespace ProjectPerseus.revit
{
    public static class ModelGuidStorage
    {
        private static readonly Guid SchemaGuid = new Guid("E0E8F560-8A87-4E0D-B4AB-ABCF01B222EE");
        private const string FieldName = "PersistentModelGuid";

        // Keyed by doc.PathName so Extensible Storage is only read once per open document.
        // Eliminates read-only-window errors from ProgressChanged / DocumentChanged callers.
        private static readonly ConcurrentDictionary<string, string> _cache
            = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Documents whose GUID could not be read or created recently. Callers in event handlers used to retry on
        // every call (hundreds of failed transactions per model open); now a failure is remembered for a while.
        private static readonly ConcurrentDictionary<string, DateTime> _failedAt
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

        internal const string ErrorGuid = "ErrorModelGuid";

        /// <summary>
        /// The document's GUID if it is already known (cached or stored), without ever creating one or opening a
        /// transaction. Safe anywhere, including DocumentChanged and linked documents. Null when there is none yet.
        /// </summary>
        public static string TryGet(Document doc)
        {
            string key = doc?.PathName;
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out string cached)) return cached;
            try
            {
                string stored = ReadStored(doc);
                if (!string.IsNullOrEmpty(stored) && !IsDetachedFromCentral(doc))
                {
                    _cache[key] = stored;
                    return stored;
                }
            }
            catch { /* unreadable right now */ }
            return null;
        }

        // A transaction can only be opened in a primary document that is neither read-only (e.g. inside
        // DocumentChanged) nor already modifiable (inside someone else's transaction).
        private static bool CanWrite(Document doc) =>
            !doc.IsLinked && !doc.IsReadOnly && !doc.IsModifiable && !doc.IsFamilyDocument;

        private static string ReadStored(Document doc)
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                Entity entity = ds.GetEntity(schema);
                if (entity.IsValid()) return entity.Get<string>(schema.GetField(FieldName));
            }
            return null;
        }

        /// <summary>
        /// Retrieves or creates a permanent GUID for the Revit document.
        /// If the document was detached or copied, it regenerates a new one.
        /// </summary>
        public static string GetOrCreate(Document doc)
        {
            string key = doc?.PathName;
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out string cached))
                return cached;
            if (doc == null) return ErrorGuid;

            // Read-only path first: an existing GUID never needs a transaction.
            string known = TryGet(doc);
            if (known != null) return known;

            if (!CanWrite(doc))
            {
                // Linked file, inside DocumentChanged, or inside another transaction: creating is impossible here.
                // Say so once per RetryAfter instead of failing a transaction on every call.
                if (!string.IsNullOrEmpty(key) &&
                    (!_failedAt.TryGetValue(key, out var at) || DateTime.UtcNow - at > RetryAfter))
                {
                    _failedAt[key] = DateTime.UtcNow;
                    Log.Info($"[ModelGuidStorage] No GUID yet and the document can't be written now " +
                             $"(linked={doc.IsLinked}, readOnly={doc.IsReadOnly}, modifiable={doc.IsModifiable}); will create it later.");
                }
                return ErrorGuid;
            }
            if (!string.IsNullOrEmpty(key) && _failedAt.TryGetValue(key, out var failed) && DateTime.UtcNow - failed < RetryAfter)
                return ErrorGuid;

            try
            {
                Schema schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
                DataStorage storage = GetOrCreateStorage(doc, schema);
                Entity entity = storage.GetEntity(schema);

                string storedGuid = entity.IsValid() ? entity.Get<string>(schema.GetField(FieldName)) : null;

                if (IsDetachedFromCentral(doc) || string.IsNullOrEmpty(storedGuid))
                {
                    string newGuid = Guid.NewGuid().ToString();
                    using (Transaction tx = new Transaction(doc, "Update Persistent Model GUID"))
                    {
                        tx.Start();
                        entity.Set(schema.GetField(FieldName), newGuid);
                        storage.SetEntity(entity);
                        tx.Commit();
                    }

                    Log.Info($"[ModelGuidStorage] New GUID assigned: {newGuid}");
                    if (!string.IsNullOrEmpty(key)) _cache[key] = newGuid;
                    return newGuid;
                }

                Log.Info($"[ModelGuidStorage] Stored GUID: {storedGuid}");
                if (!string.IsNullOrEmpty(key)) _cache[key] = storedGuid;
                return storedGuid;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(key)) _failedAt[key] = DateTime.UtcNow;
                Log.Info($"Error in ModelGuidStorage.GetOrCreate: {ex.Message}");
                return ErrorGuid;
            }
        }

        /// <summary>
        /// Removes the cached GUID for a document. Call from DocumentClosed so the
        /// next open of the same path re-reads from Extensible Storage.
        /// </summary>
        public static void ClearCache(Document doc)
        {
            string key = doc?.PathName;
            if (!string.IsNullOrEmpty(key)) { _cache.TryRemove(key, out _); _failedAt.TryRemove(key, out _); }
        }

        /// <summary>
        /// Aggressively overwrites the existing Extensible Storage GUID with a brand new one.
        /// Used specifically when a user manually clicks the "Reset Identity" button.
        /// </summary>
        public static string ForceNewInternalGuid(Document doc)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
                DataStorage storage = GetOrCreateStorage(doc, schema);
                Entity entity = storage.GetEntity(schema);

                // If the entity somehow isn't valid, instantiate a fresh one
                if (!entity.IsValid())
                {
                    entity = new Entity(schema);
                }

                string newGuid = Guid.NewGuid().ToString();

                using (Transaction tx = new Transaction(doc, "Perseus: Reset Model Identity"))
                {
                    tx.Start();
                    entity.Set(schema.GetField(FieldName), newGuid);
                    storage.SetEntity(entity);
                    tx.Commit();
                }

                Log.Info($"[ModelGuidStorage] Forced new GUID assigned: {newGuid}");
                return newGuid;
            }
            catch (Exception ex)
            {
                Log.Info($"Error in ModelGuidStorage.ForceNewInternalGuid: {ex.Message}");
                throw; // Throw this so the external command can catch it and show an error dialog
            }
        }

        // 🔹 Determines if the model has been detached from its central file
        private static bool IsDetachedFromCentral(Document doc)
        {
            try
            {
                if (!doc.IsWorkshared)
                    return false;

                ModelPath centralPath = doc.GetWorksharingCentralModelPath();
                if (centralPath == null)
                    return true; // central missing or detached

                string centralPathStr = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                return string.IsNullOrEmpty(centralPathStr);
            }
            catch
            {
                // If any exception, treat as detached (safer)
                return true;
            }
        }

        private static Schema CreateSchema()
        {
            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("ProjectPerseusModelGUID");
            builder.AddSimpleField(FieldName, typeof(string));
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("Persistent invisible GUID for identifying this Revit model.");
            return builder.Finish();
        }

        private static DataStorage GetOrCreateStorage(Document doc, Schema schema)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc).OfClass(typeof(DataStorage));

            foreach (DataStorage ds in collector)
            {
                if (ds.GetEntity(schema).IsValid())
                    return ds;
            }

            using (Transaction tx = new Transaction(doc, "Create DataStorage for Model GUID"))
            {
                tx.Start();
                DataStorage newStorage = DataStorage.Create(doc);
                newStorage.SetEntity(new Entity(schema));
                tx.Commit();
                return newStorage;
            }
        }
    }
}