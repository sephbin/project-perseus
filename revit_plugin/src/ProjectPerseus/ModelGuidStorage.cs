using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using ProjectPerseus;

namespace ProjectPerseus.revit
{
    public static class ModelGuidStorage
    {
        // 🔸 Use a fixed GUID that identifies *your add-in’s schema*
        private static readonly Guid SchemaGuid = new Guid("E0E8F560-8A87-4E0D-B4AB-ABCF01B222EE");
        private const string FieldName = "PersistentModelGuid";

        /// <summary>
        /// Retrieves or creates a permanent GUID for the Revit document.
        /// If the document was detached or copied, it regenerates a new one.
        /// </summary>
        public static string GetOrCreate(Document doc)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
                DataStorage storage = GetOrCreateStorage(doc, schema);
                Entity entity = storage.GetEntity(schema);

                // Retrieve current GUID (if present)
                string storedGuid = entity.IsValid() ? entity.Get<string>(schema.GetField(FieldName)) : null;
                
                Utl.WriteLog($"[ModelGuidStorage] Stored GUID: {storedGuid}");


                // If detached or invalid, regenerate
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

                    Utl.WriteLog($"[ModelGuidStorage] New GUID assigned: {newGuid}");
                    return newGuid;
                }

                return storedGuid;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Error in ModelGuidStorage.GetOrCreate: {ex.Message}");
                return "ErrorModelGuid";
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
