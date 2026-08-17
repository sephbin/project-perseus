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
    public static class WebHelper
    {
        public static string Post(string endpoint, string apiToken, string json, string scheme = "Bearer")
        {
            return PerformRequest(endpoint, apiToken, json, "POST", scheme);
        }

        public static string Get(string endpoint, string apiToken, string json, string scheme = "Bearer")
        {
            return PerformRequest(endpoint, apiToken, null, "GET", scheme);
        }

        public static string Put(string endpoint, string apiToken, string json, string scheme = "Bearer")
        {
            return PerformRequest(endpoint, apiToken, json, "PUT", scheme);
        }

        private static string PerformRequest(string endpoint, string apiToken, string json, string method, string scheme)
        {
            Log.Info($"[WebHelper] {method} {endpoint}");
            try
            {
                var httpWebRequest = (HttpWebRequest)WebRequest.Create(endpoint);
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = method;
                httpWebRequest.Timeout = 300000; // 5 minutes

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
