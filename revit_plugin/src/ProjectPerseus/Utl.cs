
using System;
using System.IO;
using System.Net;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Sentry;

namespace ProjectPerseus
{
    
    public class Utl
    {
        public static void WriteLog(string content)
        {
            string roamingFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appSpecificFolderPath = Path.Combine(roamingFolderPath, "ProjectPerseus");
            Directory.CreateDirectory(appSpecificFolderPath); // Creates the directory if it doesn't exist
            string filePath = Path.Combine(appSpecificFolderPath, "medusa.log");
            try
            {
                File.AppendAllText(filePath, content + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving file: {ex.Message}");
            }
        }

        public static class WebHelper
        {
            public static string Post(string endpoint, string apiToken, string json)
            {
                return PerformRequest(endpoint, apiToken, json, "POST");
            }
            public static string Get(string endpoint, string apiToken, string json)
            {
                return PerformRequest(endpoint, apiToken, null, "GET");
            }

            private static string PerformRequest(string endpoint, string apiToken, string json, string method)
            {
                try
                {
                    var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                    httpWebRequest.ContentType = "application/json";
                    httpWebRequest.Method = method;
                    httpWebRequest.Timeout = 300000; // 5 minutes

                    if (!string.IsNullOrEmpty(apiToken))
                    {
                        httpWebRequest.Headers["Authorization"] = $"Token {apiToken}";
                    }

                    // Only send a body if the method supports it
                    if (method == "POST" && !string.IsNullOrEmpty(json))
                    {
                        using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
                        {
                            streamWriter.Write(json);
                        }
                    }

                    using (var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse())
                    using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                    {
                        return streamReader.ReadToEnd();
                    }
                }
                catch (WebException ex)
                {
                    var response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            var error = reader.ReadToEnd();
                            Log.Error($"[WebHelper] {method} {endpoint} failed: {error}");
                        }
                    }

                    Log.Error($"[WebHelper] {method} {endpoint} exception: {ex.Message}");
                    throw;
                }
            }
        }
        public static void JsonDump(object o, String name)
        {
            var workingDirectory = Directory.GetCurrentDirectory();
            PrettyWriteJson(o,
                $"{workingDirectory}/{name}.json",
                null);
        }
        
        public static void PrettyWriteJson(object obj, string fileName, JsonSerializerSettings options)
        {
            var jsonString = SerializeToJson(obj, options);

            File.WriteAllText(fileName, jsonString);
        }
        
        public static string SerializeToJson(object obj, JsonSerializerSettings options = null)
        {
            //WriteLog("SerializeToJson");
            if (options is null)
            {
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            }
            //WriteLog("- SerializeToJson");
            var jsonString = "{}";
            try
            {jsonString = JsonConvert.SerializeObject(obj, Formatting.Indented, options);}
            catch (Exception ex){ WriteLog($"Error creating jsonString: {ex.Message}"); }
            
            //WriteLog("// SerializeToJson");
            return jsonString;
        }
        
        public static bool IsValidUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        public class SentryContext : IDisposable 
        {
            public SentryContext(string pingId = null)
            {
                InitAndPing(pingId);
            }

            private static void InitAndPing(string pingId = null)
            {
                Init();
                DoPing(pingId);
            }

            private static void DoPing(String pingId = null)
            {
                SentrySdk.CaptureMessage(pingId ?? "Ping");
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
            public static void Ping(String pingId = null)
            {
                using (var sentry = new SentryContext(pingId))
                {
                }
            }
        }
    }
}