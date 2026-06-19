using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.revit
{
    public static class KeyScheduleStorage
    {
        private static readonly Guid SchemaGuid = new Guid("6E8A0C4D-B1F3-4E72-A589-3DC0E4561234");
        private const string FieldName = "KeyScheduleConfigs";

        public static List<KeyScheduleConfig> Load(Document doc)
        {
            try
            {
                Schema schema = Schema.Lookup(SchemaGuid);
                if (schema == null) return new List<KeyScheduleConfig>();

                DataStorage storage = FindStorage(doc, schema);
                if (storage == null) return new List<KeyScheduleConfig>();

                Entity entity = storage.GetEntity(schema);
                if (!entity.IsValid()) return new List<KeyScheduleConfig>();

                string json = entity.Get<string>(schema.GetField(FieldName));
                if (string.IsNullOrEmpty(json)) return new List<KeyScheduleConfig>();

                return JsonConvert.DeserializeObject<List<KeyScheduleConfig>>(json)
                       ?? new List<KeyScheduleConfig>();
            }
            catch (Exception ex)
            {
                Log.Error($"[KeyScheduleStorage] Load failed: {ex.Message}");
                Log.Exception(ex);
                return new List<KeyScheduleConfig>();
            }
        }

        public static void Save(Document doc, List<KeyScheduleConfig> configs)
        {
            Schema schema = Schema.Lookup(SchemaGuid) ?? CreateSchema();
            DataStorage storage = GetOrCreateStorage(doc, schema);

            string json = JsonConvert.SerializeObject(configs);
            using (Transaction t = new Transaction(doc, "Perseus: Save Key Schedule Mappings"))
            {
                t.Start();
                Entity entity = storage.GetEntity(schema);
                if (!entity.IsValid()) entity = new Entity(schema);
                entity.Set(schema.GetField(FieldName), json);
                storage.SetEntity(entity);
                t.Commit();
            }

            Log.Info($"[KeyScheduleStorage] Saved {configs.Count} mapping(s) to extensible storage");
        }

        private static Schema CreateSchema()
        {
            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("ProjectPerseusKeySchedules");
            builder.AddSimpleField(FieldName, typeof(string));
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("Per-project key schedule to Excel/Django mappings for Perseus.");
            return builder.Finish();
        }

        private static DataStorage FindStorage(Document doc, Schema schema)
        {
            foreach (DataStorage ds in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)))
            {
                if (ds.GetEntity(schema).IsValid()) return ds;
            }
            return null;
        }

        private static DataStorage GetOrCreateStorage(Document doc, Schema schema)
        {
            DataStorage existing = FindStorage(doc, schema);
            if (existing != null) return existing;

            using (Transaction t = new Transaction(doc, "Perseus: Create Key Schedule Storage"))
            {
                t.Start();
                DataStorage ds = DataStorage.Create(doc);
                ds.SetEntity(new Entity(schema));
                t.Commit();
                return ds;
            }
        }
    }
}
