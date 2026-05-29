using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.forms;

namespace ProjectPerseus.auth
{
    public class AuthService
    {
        // ── MSAL (Entra ID) ──────────────────────────────────────────────
        private static IPublicClientApplication _msalApp;
        private static string[] _msalScopes;

        // ── JWT (self-hosted) ────────────────────────────────────────────
        private static string _jwtTokenEndpoint;
        private static string _jwtAccessToken;
        private static DateTime _jwtAccessTokenExpiry = DateTime.MinValue;

        private static readonly string _jwtRefreshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProjectPerseus", "jwt_refresh.bin");

        private static readonly string _patPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ProjectPerseus", "pat.bin");

        // ── Shared state ─────────────────────────────────────────────────
        private static string _authType = "None";
        private static bool _isInitialized = false;

        private static readonly HttpClient _http = new HttpClient();

        // ─────────────────────────────────────────────────────────────────
        // Initialise: fetch auth config from server and set up the
        // appropriate auth provider.
        // ─────────────────────────────────────────────────────────────────
        public static async Task InitializeAsync()
        {
            var baseUrl = Config.Instance.BaseUrl.TrimEnd('/');
            string configJson = Utl.WebHelper.Get($"{baseUrl}/api/auth-config/", null, null);

            if (string.IsNullOrEmpty(configJson))
                throw new Exception("Could not fetch authentication configuration from server.");

            JObject authConfig = JObject.Parse(configJson);
            _authType = authConfig["authType"]?.ToString() ?? "None";

            // ── Sandbox ──────────────────────────────────────────────────
            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                Utl.WriteLog("Auth mode: Sandbox. MSAL bypassed.");
                _isInitialized = true;
                return;
            }

            // ── Self-hosted JWT ──────────────────────────────────────────
            if (_authType.Equals("JWT", StringComparison.OrdinalIgnoreCase))
            {
                _jwtTokenEndpoint = authConfig["tokenEndpoint"]?.ToString()
                    ?? $"{baseUrl}/api/token/";
                Utl.WriteLog($"Auth mode: JWT. Endpoint: {_jwtTokenEndpoint}");
                _isInitialized = true;
                return;
            }

            // ── Entra ID (MSAL) ──────────────────────────────────────────
            string clientId = authConfig["clientId"].ToString();
            string tenantId = authConfig["tenantId"].ToString();
            _msalScopes = authConfig["scopes"].ToObject<string[]>();

            _msalApp = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .WithRedirectUri("http://localhost")
                .Build();

            var storageProperties = new StorageCreationPropertiesBuilder(
                    $"perseus_cache_{clientId}.bin",
                    MsalCacheHelper.UserRootDirectory)
                .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
            cacheHelper.RegisterCache(_msalApp.UserTokenCache);

            Utl.WriteLog($"Auth mode: EntraID. Tenant: {tenantId}");
            _isInitialized = true;
        }

        // ─────────────────────────────────────────────────────────────────
        // GetTokenAsync: returns a Bearer token for the current auth mode.
        // ─────────────────────────────────────────────────────────────────
        public static async Task<string> GetTokenAsync()
        {
            // Personal API token takes priority over all server-driven auth modes.
            // Allows headless/batch use without interactive OAuth prompts.
            string pat = LoadPersonalApiToken();
            if (!string.IsNullOrEmpty(pat))
                return pat;

            if (!_isInitialized)
                await InitializeAsync();

            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
                return "sandbox-mode";

            if (_authType.Equals("JWT", StringComparison.OrdinalIgnoreCase))
                return await GetJwtTokenAsync();

            // ── MSAL silent → interactive fallback ───────────────────────
            var accounts = await _msalApp.GetAccountsAsync();
            AuthenticationResult result;
            try
            {
                result = await _msalApp
                    .AcquireTokenSilent(_msalScopes, accounts.FirstOrDefault())
                    .ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
                try
                {
                    result = await AcquireTokenInteractiveOnStaAsync();
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Interactive MSAL login failed: {ex.Message}");
                    return null;
                }
            }
            return result.AccessToken;
        }

        // ─────────────────────────────────────────────────────────────────
        // Run MSAL interactive auth on a dedicated STA thread.
        //
        // MSAL's system-browser flow uses Process.Start + a loopback TCP
        // listener. On a thread-pool (MTA) thread inside Revit this can
        // conflict with Revit's COM/shell environment and crash the process.
        // Running it on a clean STA thread with no SynchronizationContext
        // isolates it completely. A TaskCompletionSource lets the caller
        // await the result without blocking any thread pool threads.
        // ─────────────────────────────────────────────────────────────────
        private static Task<AuthenticationResult> AcquireTokenInteractiveOnStaAsync()
        {
            var tcs = new TaskCompletionSource<AuthenticationResult>();

            var sta = new Thread(() =>
            {
                // No SynchronizationContext: async continuations inside MSAL
                // run on thread-pool threads, not back here — no deadlock risk.
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(3)))
                    {
                        var r = _msalApp
                            .AcquireTokenInteractive(_msalScopes)
                            .WithUseEmbeddedWebView(false)
                            .WithSystemWebViewOptions(new SystemWebViewOptions
                            {
                                HtmlMessageSuccess =
                                    "<html><head><script>window.onload=function(){window.close();}" +
                                    "</script></head><body><p>Authentication complete. " +
                                    "This window will close automatically.</p></body></html>",
                            })
                            .ExecuteAsync(cts.Token)
                            .ConfigureAwait(false)
                            .GetAwaiter()
                            .GetResult();
                        tcs.SetResult(r);
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            sta.SetApartmentState(ApartmentState.STA);
            sta.IsBackground = true; // doesn't keep Revit alive if it shuts down
            sta.Start();

            return tcs.Task;
        }

        // ─────────────────────────────────────────────────────────────────
        // Sync wrapper — safe to call from non-async Revit event handlers.
        // ─────────────────────────────────────────────────────────────────
        public static string GetAuthTokenSafely()
        {
            try
            {
                return Task.Run(async () => await GetTokenAsync())
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to retrieve auth token: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Returns "Token" when a PAT is stored (Django TokenAuthentication),
        // "Bearer" for all OAuth2/JWT/sandbox flows.
        // Use alongside GetAuthTokenSafely() to build the Authorization header.
        // ─────────────────────────────────────────────────────────────────
        public static string GetAuthSchemeSafely()
        {
            return HasPersonalApiToken() ? "Token" : "Bearer";
        }

        // ── Personal API Token management ────────────────────────────────

        public static void StorePersonalApiToken(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_patPath));
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), null,
                    DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_patPath, encrypted);
                Utl.WriteLog("Personal API token stored (DPAPI-encrypted).");
            }
            catch (Exception ex) { Utl.WriteLog($"Failed to store PAT: {ex.Message}"); }
        }

        public static bool HasPersonalApiToken() => File.Exists(_patPath);

        public static void ClearPersonalApiToken()
        {
            try { if (File.Exists(_patPath)) File.Delete(_patPath); }
            catch (Exception ex) { Utl.WriteLog($"Failed to clear PAT: {ex.Message}"); }
        }

        private static string LoadPersonalApiToken()
        {
            try
            {
                if (!File.Exists(_patPath)) return null;
                var encrypted = File.ReadAllBytes(_patPath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        public static void Reset()
        {
            _msalApp = null;
            _isInitialized = false;
            _authType = "None";
            _jwtTokenEndpoint = null;
            _jwtAccessToken = null;
            _jwtAccessTokenExpiry = DateTime.MinValue;
            // PAT is intentionally not cleared on Reset — it persists across server URL changes.
        }

        // ═════════════════════════════════════════════════════════════════
        // JWT helpers
        // ═════════════════════════════════════════════════════════════════

        private static async Task<string> GetJwtTokenAsync()
        {
            // Return cached access token if still valid (with 30-second buffer)
            if (_jwtAccessToken != null && DateTime.UtcNow < _jwtAccessTokenExpiry.AddSeconds(-30))
                return _jwtAccessToken;

            // Try silent refresh using stored refresh token
            string refreshToken = LoadRefreshToken();
            if (refreshToken != null)
            {
                string refreshed = await TryRefreshJwtAsync(refreshToken);
                if (refreshed != null)
                    return refreshed;
            }

            // Silent refresh failed or no stored token — show login form
            return await AcquireJwtViaLoginFormAsync();
        }

        private static async Task<string> TryRefreshJwtAsync(string refreshToken)
        {
            try
            {
                var body = new StringContent(
                    JsonConvert.SerializeObject(new { refresh = refreshToken }),
                    Encoding.UTF8, "application/json");

                var response = await _http.PostAsync(_jwtTokenEndpoint.TrimEnd('/') + "/refresh/", body);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                string access = json["access"]?.ToString();
                if (string.IsNullOrEmpty(access))
                    return null;

                CacheAccessToken(access);
                return access;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"JWT silent refresh failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> AcquireJwtViaLoginFormAsync()
        {
            // WinForms must run on an STA thread
            (string username, string password)? credentials = null;
            var sta = new Thread(() =>
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                var form = new JwtLoginForm(Config.Instance.BaseUrl);
                if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    credentials = (form.Username, form.Password);
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();

            if (credentials == null)
            {
                Utl.WriteLog("JWT login cancelled by user.");
                return null;
            }

            try
            {
                var body = new StringContent(
                    JsonConvert.SerializeObject(new { username = credentials.Value.username, password = credentials.Value.password }),
                    Encoding.UTF8, "application/json");

                var response = await _http.PostAsync(_jwtTokenEndpoint, body);
                if (!response.IsSuccessStatusCode)
                {
                    Utl.WriteLog($"JWT login failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                string access = json["access"]?.ToString();
                string refresh = json["refresh"]?.ToString();

                if (string.IsNullOrEmpty(access))
                    return null;

                CacheAccessToken(access);
                if (!string.IsNullOrEmpty(refresh))
                    StoreRefreshToken(refresh);

                return access;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"JWT login request failed: {ex.Message}");
                return null;
            }
        }

        private static void CacheAccessToken(string token)
        {
            _jwtAccessToken = token;
            _jwtAccessTokenExpiry = ParseJwtExpiry(token);
        }

        // ── Refresh token persistence (DPAPI) ────────────────────────────

        private static void StoreRefreshToken(string token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_jwtRefreshPath));
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(token), null,
                    DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_jwtRefreshPath, encrypted);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to store JWT refresh token: {ex.Message}");
            }
        }

        private static string LoadRefreshToken()
        {
            try
            {
                if (!File.Exists(_jwtRefreshPath))
                    return null;
                var encrypted = File.ReadAllBytes(_jwtRefreshPath);
                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        // ── Decode JWT payload to extract exp claim (no signature check) ─

        private static DateTime ParseJwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return DateTime.UtcNow.AddMinutes(4);
                string payload = parts[1];
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                long exp = json["exp"]?.Value<long>() ?? 0;
                return exp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime
                    : DateTime.UtcNow.AddMinutes(4);
            }
            catch
            {
                return DateTime.UtcNow.AddMinutes(4);
            }
        }
    }
}
