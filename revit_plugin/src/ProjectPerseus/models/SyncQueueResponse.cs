using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProjectPerseus.models
{
    public class SyncQueueResponse
    {
        [JsonProperty("queue")]
        public List<string> Queue { get; set; } = new List<string>();
    }
}
