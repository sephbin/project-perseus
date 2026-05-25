
using System;
using System.IO;
using System.Net;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Sentry;

namespace ProjectPerseus
{
    public enum LogLevel { Info, Warn, Error }

    public class Utl
    {
        private static string _sessionLogPath;
        private static readonly object _logLock = new object();

        public static void InitSession(string revitVersion, string pluginVersion)
        {
            string logsFolder = GetLogsFolder();
            PurgeLogs(logsFolder, daysToKeep: 30);

            string username = Environment.UserName;
            _sessionLogPath = Path.Combine(logsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{username}-medusa.log");

            string header =
                "=== Perseus Session Log ===" + Environment.NewLine +
                $"Start   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                $"User    : {username}" + Environment.NewLine +
                $"Machine : {Environment.MachineName}" + Environment.NewLine +
                $"Revit   : {revitVersion}" + Environment.NewLine +
                $"Plugin  : {pluginVersion}" + Environment.NewLine +
                "===========================" + Environment.NewLine +
                Environment.NewLine;

            lock (_logLock)
            {
                File.WriteAllText(_sessionLogPath, header);
            }
        }

        private static string GetLogsFolder()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectPerseus", "logs");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void PurgeLogs(string folder, int daysToKeep)
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-daysToKeep);
                foreach (string file in Directory.GetFiles(folder, "*-medusa.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch { }
        }

        public static void WriteLog(string content, LogLevel level = LogLevel.Info)
        {
            string levelTag = level == LogLevel.Error ? "[ERROR]" :
                              level == LogLevel.Warn  ? "[WARN] " : "[INFO] ";
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"{timestamp}\t{levelTag}\t{content}{Environment.NewLine}";

            string path = _sessionLogPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectPerseus", "medusa.log");

            lock (_logLock)
            {
                try
                {
                    File.AppendAllText(path, logEntry);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving log: {ex.Message}");
                }
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
                        httpWebRequest.Headers["Authorization"] = $"Bearer {apiToken}";
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
            WriteLog("SerializeToJson");
            if (options is null)
            {
                options = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
            }
            WriteLog("- SerializeToJson");
            var jsonString = "{}";
            try
            {jsonString = JsonConvert.SerializeObject(obj, Formatting.None, options);}
            catch (Exception ex){ WriteLog($"Error creating jsonString: {ex.Message}"); }

            WriteLog("// SerializeToJson");
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