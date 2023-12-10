using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RevitSyncPlugin
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

        public class IgnorePropertiesResolver : DefaultContractResolver
        {
            private readonly HashSet<string> ignoreProps;
            public IgnorePropertiesResolver(IEnumerable<string> propNamesToIgnore)
            {
                this.ignoreProps = new HashSet<string>(propNamesToIgnore);
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);
                if (this.ignoreProps.Contains(property.PropertyName))
                {
                    property.ShouldSerialize = _ => false;
                }
                return property;
            }
        }
        
        public class IncludeOnlyPropertiesResolver : DefaultContractResolver
        {
            private readonly HashSet<string> includeProps;
            public IncludeOnlyPropertiesResolver(IEnumerable<string> propNamesToInclude)
            {
                this.includeProps = new HashSet<string>(propNamesToInclude);
            }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                JsonProperty property = base.CreateProperty(member, memberSerialization);
                if (!this.includeProps.Contains(property.PropertyName))
                {
                    property.ShouldSerialize = _ => false;
                }
                return property;
            }
        }
    }
}