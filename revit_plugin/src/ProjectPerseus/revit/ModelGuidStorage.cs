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

        /// <summary>
        /// Retrieves or creates a permanent GUID for the Revit document.
        /// If the document was detached or copied, it regenerates a new one.
        /// </summary>
        public static string GetOrCreate(Document doc)
        {
            string key = doc?.PathName;
            if (!string.IsNullOrEmpty(key) && _cache.TryGetValue(key, out string cached))
                return cached;

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
                Log.Info($"Error in ModelGuidStorage.GetOrCreate: {ex.Message}");
                return "ErrorModelGuid";
            }
        }

        /// <summary>
        /// Removes the cached GUID for a document. Call from DocumentClosed so the
        /// next open of the same path re-reads from Extensible Storage.
        /// </summary>
        public static void ClearCache(Document doc)
        {
            string key = doc?.PathName;
            if (!string.IsNullOrEmpty(key)) _cache.TryRemove(key, out _);
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