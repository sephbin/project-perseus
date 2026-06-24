using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.config;
using ProjectPerseus.ui;

using ProjectPerseus.logging;
namespace ProjectPerseus.auth
{
    // Self-hosted JWT provider. The Django backend exposes /api/token/ (login) and
    // /api/token/refresh/ for SimpleJWT-style auth. Access token is cached in memory until
    // it expires; refresh token survives process exit via TokenCache (DPAPI-encrypted).
    internal static class JwtAuth
    {
        private static string _tokenEndpoint;
        private static string _cachedAccessToken;
        private static DateTime _cachedAccessExpiry = DateTime.MinValue;

        private static readonly HttpClient _http = new HttpClient();

        public static void Initialize(JObject authConfig, string baseUrl)
        {
            _tokenEndpoint = authConfig["tokenEndpoint"]?.ToString()
                ?? $"{baseUrl}/api/token/";
            Log.Info($"Auth mode: JWT. Endpoint: {_tokenEndpoint}");
        }

        public static async Task<string> GetTokenAsync()
        {
            // Return cached access token if still valid (30-second buffer).
            if (_cachedAccessToken != null && DateTime.UtcNow < _cachedAccessExpiry.AddSeconds(-30))
                return _cachedAccessToken;

            string refreshToken = TokenCache.LoadRefreshToken();
            if (refreshToken != null)
            {
                string refreshed = await TryRefreshAsync(refreshToken);
                if (refreshed != null)
                    return refreshed;
            }

            // Silent refresh failed or no stored token — prompt for credentials.
            return await AcquireViaLoginFormAsync();
        }

        // Silent-only variant — returns null rather than showing the login form.
        // Used by background/automatic operations that must never show UI.
        public static async Task<string> GetTokenSilentAsync()
        {
            if (_cachedAccessToken != null && DateTime.UtcNow < _cachedAccessExpiry.AddSeconds(-30))
                return _cachedAccessToken;

            string refreshToken = TokenCache.LoadRefreshToken();
            if (refreshToken != null)
                return await TryRefreshAsync(refreshToken);

            return null; // no credentials cached — caller should let user authenticate normally
        }

        public static void Reset()
        {
            _tokenEndpoint = null;
            _cachedAccessToken = null;
            _cachedAccessExpiry = DateTime.MinValue;
        }

        private static async Task<string> TryRefreshAsync(string refreshToken)
        {
            try
            {
                var body = new StringContent(
                    JsonConvert.SerializeObject(new { refresh = refreshToken }),
                    Encoding.UTF8, "application/json");

                Log.Info($"[JwtAuth] POST {_tokenEndpoint.TrimEnd('/')}/refresh/");
                var response = await _http.PostAsync(_tokenEndpoint.TrimEnd('/') + "/refresh/", body);
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
                Log.Info($"JWT silent refresh failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> AcquireViaLoginFormAsync()
        {
            // WinForms must run on an STA thread.
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
                Log.Info("JWT login cancelled by user.");
                return null;
            }

            try
            {
                var body = new StringContent(
                    JsonConvert.SerializeObject(new { username = credentials.Value.username, password = credentials.Value.password }),
                    Encoding.UTF8, "application/json");

                Log.Info($"[JwtAuth] POST {_tokenEndpoint}");
                var response = await _http.PostAsync(_tokenEndpoint, body);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Info($"JWT login failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                string access = json["access"]?.ToString();
                string refresh = json["refresh"]?.ToString();

                if (string.IsNullOrEmpty(access))
                    return null;

                CacheAccessToken(access);
                if (!string.IsNullOrEmpty(refresh))
                    TokenCache.StoreRefreshToken(refresh);

                return access;
            }
            catch (Exception ex)
            {
                Log.Info($"JWT login request failed: {ex.Message}");
                return null;
            }
        }

        private static void CacheAccessToken(string token)
        {
            _cachedAccessToken = token;
            _cachedAccessExpiry = ParseExpiry(token);
        }

        // Decode JWT payload to extract the `exp` claim. No signature check — we trust
        // the token because we just received it from our own server over TLS. Falls back
        // to a 4-minute window if parsing fails, which is short enough to force a refresh
        // soon but long enough to avoid hammering the server.
        private static DateTime ParseExpiry(string token)
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
