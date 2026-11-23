using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.auth
{
    /// <summary>
    /// Browser-pairing session ("PerseusSession" token) for edits made from Revit to settings that live on the server
    /// (Project Rules, Auto Parameters...). The user authorises once in the browser (django-perseus auth_pair_browser,
    /// signed in to the web app); the token isn't tied to a model and lasts 8 h server-side, so it is shared by every
    /// editor for the Revit session, per server. Background threads only (blocks while polling).
    /// </summary>
    internal static class PairingSession
    {
        private static readonly Dictionary<string, (string Token, DateTime Expiry)> _sessions
            = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new object();

        internal static string Cached(string baseUrl)
        {
            lock (_lock)
                return _sessions.TryGetValue(Key(baseUrl), out var s) && DateTime.UtcNow < s.Expiry ? s.Token : null;
        }

        internal static void Forget(string baseUrl)
        {
            lock (_lock) _sessions.Remove(Key(baseUrl));
        }

        /// <summary>Opens the browser to authorise and waits (up to 3 min). Null on failure; <paramref name="status"/> says why.</summary>
        internal static string Acquire(string baseUrl, string sourceGuid, Action<string, bool> status)
        {
            baseUrl = Key(baseUrl);
            status?.Invoke("Opening the browser to authorise…", false);
            string code, pairUrl;
            try
            {
                string body = JsonConvert.SerializeObject(new { source_guid = sourceGuid });
                var init = JObject.Parse(WebHelper.Post($"{baseUrl}/api/auth/pair/initiate/", null, body));
                code = init["code"]?.ToString();
                pairUrl = init["pair_url"]?.ToString();
            }
            catch (Exception ex)
            {
                status?.Invoke($"Could not start authorisation: {ex.Message}", true);
                return null;
            }
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(pairUrl))
            {
                status?.Invoke("Invalid authorisation response from the server.", true);
                return null;
            }

            try { Process.Start(new ProcessStartInfo(pairUrl) { UseShellExecute = true }); }
            catch (Exception ex) { status?.Invoke($"Could not open the browser: {ex.Message}", true); return null; }

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2500);
                try
                {
                    var st = JObject.Parse(WebHelper.Get($"{baseUrl}/api/auth/pair/{code}/status/", null, null));
                    string s = st["status"]?.ToString();
                    string token = st["session_token"]?.ToString();
                    if (s == "authenticated" && !string.IsNullOrEmpty(token))
                    {
                        lock (_lock) _sessions[baseUrl] = (token, DateTime.UtcNow.AddHours(7.5)); // server: 8 h
                        status?.Invoke("Authorised.", false);
                        return token;
                    }
                    if (s == "expired")
                    {
                        status?.Invoke("The authorisation request expired. Please try again.", true);
                        return null;
                    }
                }
                catch { /* transient: keep polling */ }
                int left = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                status?.Invoke($"Waiting for you to authorise in the browser… ({left}s)", false);
            }
            status?.Invoke("Authorisation timed out. Please try again.", true);
            return null;
        }

        /// <summary>
        /// PUT with the session, authorising first when there is none; a 401 on a cached session (revoked / expired
        /// early) authorises once more and retries. Returns the response body, or null when authorisation failed.
        /// Other HTTP errors (400 validation, 403 no edit rights) are thrown as WebException for the caller to show.
        /// </summary>
        internal static string Put(string baseUrl, string sourceGuid, string endpoint, string body, Action<string, bool> status) =>
            Send(baseUrl, sourceGuid, status, token => WebHelper.Put(endpoint, token, body, "PerseusSession"));

        /// <summary>POST with the session; same authorisation / retry behaviour as Put.</summary>
        internal static string Post(string baseUrl, string sourceGuid, string endpoint, string body, Action<string, bool> status) =>
            Send(baseUrl, sourceGuid, status, token => WebHelper.Post(endpoint, token, body, "PerseusSession"));

        private static string Send(string baseUrl, string sourceGuid, Action<string, bool> status, Func<string, string> request)
        {
            string token = Cached(baseUrl);
            bool fresh = false;
            if (token == null)
            {
                token = Acquire(baseUrl, sourceGuid, status);
                if (token == null) return null;
                fresh = true;
            }
            try
            {
                return request(token);
            }
            catch (WebException wex) when (!fresh && StatusOf(wex) == HttpStatusCode.Unauthorized)
            {
                Log.Info("[PairingSession] cached session rejected (401); authorising again.");
                Forget(baseUrl);
                token = Acquire(baseUrl, sourceGuid, status);
                if (token == null) return null;
                return request(token);
            }
        }

        internal static HttpStatusCode? StatusOf(WebException wex) => (wex.Response as HttpWebResponse)?.StatusCode;

        /// <summary>Body of an HTTP error response (Django's {"errors": [...]} etc.), or null.</summary>
        internal static string ErrorBody(WebException wex) => wex?.Data[WebHelper.ResponseBodyKey] as string;

        /// <summary>Readable message for a failed request: Django's {"errors": [...]} lines, a plain-text body, else the exception.</summary>
        internal static string Describe(Exception ex)
        {
            if (ex is WebException wex)
            {
                string body = ErrorBody(wex);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        if (JObject.Parse(body)["errors"] is JArray errs && errs.Count > 0)
                            return string.Join("\n", errs);
                    }
                    catch { /* not JSON */ }
                    if (body.Length < 400 && !body.TrimStart().StartsWith("<")) return body.Trim();
                }
            }
            return ex.Message;
        }

        private static string Key(string baseUrl) => (baseUrl ?? "").TrimEnd('/');
    }
}
