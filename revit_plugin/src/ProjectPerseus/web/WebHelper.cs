using System;
using System.IO;
using System.Net;

using ProjectPerseus.logging;
namespace ProjectPerseus.web
{
    // Shared HTTP helper for JSON requests against Django. Originally lived as duplicated
    // nested classes inside Utl and ProjectPerseusWeb; consolidated in P5 (2026-05-30).
    //
    // The `scheme` parameter exists so the legacy ProjectPerseus token-auth path
    // (ProjectPerseusWeb.SubmitElementState) can pass "Token" — every other caller
    // takes the default "Bearer" used by MSAL/JWT.
    //
    // timeoutMs: the default (5 minutes) suits uploads on background threads. Anything called on Revit's UI
    // thread, above all from the pre-sync hook, must pass a short timeout (see SyncTimeoutMs): with the default a
    // slow server froze Revit for minutes.
    public static class WebHelper
    {
        public const int DefaultTimeoutMs = 300000;
        // For calls made while Revit's UI thread waits (pre-sync checks): fail fast, the sync then proceeds.
        public const int SyncTimeoutMs = 5000;

        public static string Post(string endpoint, string apiToken, string json, string scheme = "Bearer", int timeoutMs = DefaultTimeoutMs)
        {
            return PerformRequest(endpoint, apiToken, json, "POST", scheme, timeoutMs);
        }

        public static string Get(string endpoint, string apiToken, string json, string scheme = "Bearer", int timeoutMs = DefaultTimeoutMs)
        {
            return PerformRequest(endpoint, apiToken, null, "GET", scheme, timeoutMs);
        }

        public static string Put(string endpoint, string apiToken, string json, string scheme = "Bearer", int timeoutMs = DefaultTimeoutMs)
        {
            return PerformRequest(endpoint, apiToken, json, "PUT", scheme, timeoutMs);
        }

        private static string PerformRequest(string endpoint, string apiToken, string json, string method, string scheme, int timeoutMs)
        {
            Log.Info($"[WebHelper] {method} {endpoint}");
            try
            {
                var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = method;
                httpWebRequest.Timeout = timeoutMs;
                httpWebRequest.ReadWriteTimeout = timeoutMs;

                if (!string.IsNullOrEmpty(apiToken))
                {
                    httpWebRequest.Headers["Authorization"] = $"{scheme} {apiToken}";
                }

                if ((method == "POST" || method == "PUT") && !string.IsNullOrEmpty(json))
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
}
