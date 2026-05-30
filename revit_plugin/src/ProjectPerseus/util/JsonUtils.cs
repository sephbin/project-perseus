using System;
using System.IO;
using Newtonsoft.Json;
using ProjectPerseus.logging;

namespace ProjectPerseus.util
{
    // JSON serialization helpers extracted from Utl in P7. SerializeToJson is the
    // workhorse used by both the file-export sync runners and ProjectPerseusWeb.
    public static class JsonUtils
    {
        public static string SerializeToJson(object obj, JsonSerializerSettings options = null)
        {
            Log.Info("SerializeToJson");
            if (options is null)
            {
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            }
            Log.Info("- SerializeToJson");
            var jsonString = "{}";
            try
            {
                jsonString = JsonConvert.SerializeObject(obj, Formatting.None, options);
            }
            catch (Exception ex)
            {
                Log.Info($"Error creating jsonString: {ex.Message}");
            }

            Log.Info("// SerializeToJson");
            return jsonString;
        }

        public static void PrettyWriteJson(object obj, string fileName, JsonSerializerSettings options)
        {
            var jsonString = SerializeToJson(obj, options);
            File.WriteAllText(fileName, jsonString);
        }

        public static void JsonDump(object o, string name)
        {
            var workingDirectory = Directory.GetCurrentDirectory();
            PrettyWriteJson(o, $"{workingDirectory}/{name}.json", null);
        }
    }
}
