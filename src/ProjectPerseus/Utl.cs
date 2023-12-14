
using System.IO;
using Newtonsoft.Json;

namespace ProjectPerseus
{
    public class Utl
    {
        public static void PrettyWriteJson(object obj, string fileName, JsonSerializerSettings options)
        {
            var jsonString = SerializeToString(obj, options);

            File.WriteAllText(fileName, jsonString);
        }

        public static string SerializeToString(object obj, JsonSerializerSettings options)
        {
            if (options is null)
            {
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            }

            var jsonString = JsonConvert.SerializeObject(obj, Formatting.Indented, options);
            return jsonString;
        }
    }
}