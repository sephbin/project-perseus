using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProjectPerseus.logging;

namespace ProjectPerseus.aps
{
    /// <summary>
    /// Autodesk sign-in for APS (3-legged OAuth v2, authorization code + PKCE, public client: no secret). Interactive
    /// sign-in opens the system browser and catches the redirect on a localhost HttpListener (ApsConfig.CallbackUrl).
    /// The refresh token is kept DPAPI-encrypted at %AppData%\ProjectPerseus\aps_token.bin (current Windows user only);
    /// the access token in memory. Scope: data:read. Never call on Revit's UI thread: it awaits HTTP.
    /// </summary>
    internal static class ApsAuth
    {
        private const string AuthorizeUrl = "https://developer.api.autodesk.com/authentication/v2/authorize";
        private const string TokenUrl = "https://developer.api.autodesk.com/authentication/v2/token";
        private const string Scope = "data:read";

        private static readonly string TokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "aps_token.bin");
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static string _access;
        private static DateTime _accessExpiresUtc;

        /// <summary>A refresh token is stored (the user signed in before), so listing can run without a browser.</summary>
        internal static bool HasSession => _access != null || File.Exists(TokenPath);

        internal static void SignOut()
        {
            _access = null;
            try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { /* ignore */ }
        }

        /// <summary>
        /// A valid access token: cached, else refreshed, else (interactive only) a browser sign-in. Null when not
        /// signed in and !interactive.
        /// </summary>
        internal static async Task<string> GetTokenAsync(bool interactive, CancellationToken ct = default(CancellationToken))
        {
            var cfg = ApsConfig.Load();
            if (!cfg.IsSet) throw new InvalidOperationException("No Autodesk (APS) app is set up for Perseus yet.");
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_access != null && DateTime.UtcNow < _accessExpiresUtc) return _access;
                string refresh = LoadRefresh();
                if (refresh != null)
                {
                    try
                    {
                        await RequestToken(cfg, new Dictionary<string, string>
                        {
                            { "grant_type", "refresh_token" }, { "refresh_token", refresh }, { "client_id", cfg.ClientId }, { "scope", Scope },
                        }, ct).ConfigureAwait(false);
                        return _access;
                    }
                    catch (Exception ex) { Log.Info($"[APS] refresh failed, signing in again: {ex.Message}"); SignOut(); }
                }
                if (!interactive) return null;
                await SignInInteractive(cfg, ct).ConfigureAwait(false);
                return _access;
            }
            finally { Gate.Release(); }
        }

        private static async Task SignInInteractive(ApsConfig cfg, CancellationToken ct)
        {
            string verifier = Base64Url(RandomBytes(48));
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomBytes(16));

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://localhost:{cfg.CallbackPort}/perseus/aps/");
                listener.Start();
                string url = $"{AuthorizeUrl}?response_type=code&client_id={Uri.EscapeDataString(cfg.ClientId)}" +
                             $"&redirect_uri={Uri.EscapeDataString(cfg.CallbackUrl)}&scope={Uri.EscapeDataString(Scope)}" +
                             $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                // Wait for the redirect (5 minutes), ignoring stray requests such as favicon.
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (true)
                {
                    var getCtx = listener.GetContextAsync();
                    var done = await Task.WhenAny(getCtx, Task.Delay(deadline - DateTime.UtcNow, ct)).ConfigureAwait(false);
                    if (done != getCtx) throw new TimeoutException("Autodesk sign-in wasn't completed in the browser.");
                    var ctx = getCtx.Result;
                    var q = ctx.Request.QueryString;
                    if (q["code"] == null && q["error"] == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                    bool ok = q["error"] == null && q["state"] == state;
                    Reply(ctx, ok ? "Signed in to Autodesk. You can close this tab and go back to Revit."
                                  : "Autodesk sign-in failed: " + (q["error_description"] ?? q["error"] ?? "state mismatch"));
                    if (!ok) throw new InvalidOperationException("Autodesk sign-in failed: " + (q["error_description"] ?? q["error"] ?? "state mismatch"));
                    await RequestToken(cfg, new Dictionary<string, string>
                    {
                        { "grant_type", "authorization_code" }, { "code", q["code"] }, { "redirect_uri", cfg.CallbackUrl },
                        { "code_verifier", verifier }, { "client_id", cfg.ClientId },
                    }, ct).ConfigureAwait(false);
                    Log.Info("[APS] signed in");
                    return;
                }
            }
        }

        private static async Task RequestToken(ApsConfig cfg, Dictionary<string, string> form, CancellationToken ct)
        {
            using (var resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"token request {(int)resp.StatusCode}: {body}");
                var j = JObject.Parse(body);
                _access = (string)j["access_token"];
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(((int?)j["expires_in"] ?? 3600) - 120);
                string refresh = (string)j["refresh_token"];
                if (!string.IsNullOrEmpty(refresh)) StoreRefresh(refresh);   // refresh tokens rotate: keep the newest
            }
        }

        private static void Reply(HttpListenerContext ctx, string text)
        {
            try
            {
                var html = Encoding.UTF8.GetBytes($"<html><body style='font-family:Segoe UI;margin:40px'><h3>Perseus</h3><p>{WebUtility.HtmlEncode(text)}</p></body></html>");
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.OutputStream.Write(html, 0, html.Length);
                ctx.Response.Close();
            }
            catch { /* browser gone */ }
        }

        private static void StoreRefresh(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TokenPath));
                File.WriteAllBytes(TokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) { Log.Info($"[APS] could not store the refresh token: {ex.Message}"); }
        }

        private static string LoadRefresh()
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
