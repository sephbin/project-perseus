using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    internal class AlertDto
    {
        [JsonProperty("id")]           public long   Id          { get; set; }
        [JsonProperty("title")]        public string Title       { get; set; }
        [JsonProperty("body")]         public string Body        { get; set; }
        [JsonProperty("project_name")] public string ProjectName { get; set; }
        [JsonProperty("source_name")]  public string SourceName  { get; set; }
    }
}
