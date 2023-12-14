using System.IO;
using Newtonsoft.Json;

namespace ProjectPerseus
{
    public class Config
    {
        public string ApiToken { get; set; }
        public string BaseUrl { get; set; }

        public string ElementsEndpoint => $"{BaseUrl}/rapi/elements/";

        public static Config Load(string configPath)
        {
            if (!File.Exists(configPath)) throw new FileNotFoundException("Config file not found");
            var jsonString = File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<Config>(jsonString);
            return config;
        }
        
        public void Save(string fileName)
        {
            var jsonString = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(fileName, jsonString);
        }
    }
}