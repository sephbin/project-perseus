using System;
using System.IO;
using System.Text;
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
            if (options is null)
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            try
            {
                return JsonConvert.SerializeObject(obj, Formatting.None, options);
            }
            catch (Exception ex)
            {
                Log.Info($"SerializeToJson error: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// Streams obj directly to a file without materialising the full JSON string in memory.
        /// Use this for large collections (thousands of elements) to avoid GC pressure.
        /// </summary>
        public static void WriteToFile(object obj, string path, JsonSerializerSettings options = null)
        {
            if (options is null)
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

            var serializer = JsonSerializer.CreateDefault(options);
            serializer.Formatting = Formatting.None;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            using (var jw = new JsonTextWriter(sw) { AutoCompleteOnClose = true })
            {
                serializer.Serialize(jw, obj);
            }
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
