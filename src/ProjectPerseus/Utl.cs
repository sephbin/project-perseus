
using System;
using System.IO;
using Newtonsoft.Json;
using Sentry;

namespace ProjectPerseus
{
    public class Utl
    {
        public static void JsonDump(object o, String name)
        {
            var workingDirectory = Directory.GetCurrentDirectory();
            PrettyWriteJson(o,
                $"{workingDirectory}/{name}.json",
                null);
        }
        
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
        
        public static bool IsValidUrl(string url)
        {
            Uri uriResult;
            return Uri.TryCreate(url, UriKind.Absolute, out uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        public class Sentry : IDisposable 
        {
            public Sentry()
            {
                InitAndPing();
            }

            private static void InitAndPing()
            {
                Init();
                SentrySdk.CaptureMessage("Ping");
            }

            private static void Init()
            {
                SentrySdk.Init(options =>
                {
                    // A Sentry Data Source Name (DSN) is required.
                    // See https://docs.sentry.io/product/sentry-basics/dsn-explainer/
                    // You can set it in the SENTRY_DSN environment variable, or you can set it in code here.
                    options.Dsn = "https://32e0a3644c9e180de912d15cae6df17c@o4506516579155968.ingest.sentry.io/4506516583546880";

                    // This option is recommended. It enables Sentry's "Release Health" feature.
                    options.AutoSessionTracking = true;

                    // This option is recommended for client applications only. It ensures all threads use the same global scope.
                    // If you're writing a background service of any kind, you should remove this.
                    options.IsGlobalModeEnabled = true;

                    // This option will enable Sentry's tracing features. You still need to start transactions and spans.
                    options.EnableTracing = true;
                });
            }

            public void Dispose()
            {
                SentrySdk.Close();
            }
            
            /// <summary>
            /// For standalone pinging of Sentry
            /// </summary>
            public static void Ping()
            {
                using (var sentry = new Utl.Sentry())
                {
                }
            }
        }
    }
}