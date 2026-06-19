using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProjectPerseus.config;
using ProjectPerseus.web;

using ProjectPerseus.logging;
namespace ProjectPerseus.auth
{
    // Server-driven auth coordinator. Asks the Django backend which auth mode to use
    // (None / JWT / EntraID) and dispatches token requests to the appropriate provider.
    // A stored PersonalAccessToken always wins — it short-circuits any server-driven flow
    // and enables headless/batch use.
    //
    // Public API surface (do not break — called from SyncOrchestrator, FullSyncRunner,
    // ProjectPerseusWeb, SettingsForm):
    //   GetAuthTokenSafely(), GetAuthSchemeSafely(),
    //   HasPersonalApiToken(), StorePersonalApiToken(), ClearPersonalApiToken(),
    //   Reset(), InitializeAsync(), GetTokenAsync().
    public class AuthService
    {
        private static string _authType = "None";
        private static bool _isInitialized = false;

        public static async Task InitializeAsync()
        {
            var baseUrl = Config.Instance.BaseUrl.TrimEnd('/');
            string configJson = WebHelper.Get($"{baseUrl}/api/auth-config/", null, null);

            if (string.IsNullOrEmpty(configJson))
                throw new Exception("Could not fetch authentication configuration from server.");

            JObject authConfig = JObject.Parse(configJson);
            _authType = authConfig["authType"]?.ToString() ?? "None";

            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("Auth mode: Sandbox. MSAL bypassed.");
                _isInitialized = true;
                return;
            }

            if (_authType.Equals("JWT", StringComparison.OrdinalIgnoreCase))
            {
                JwtAuth.Initialize(authConfig, baseUrl);
                _isInitialized = true;
                return;
            }

            // Default to Entra ID (MSAL).
            await MsalAuth.InitializeAsync(authConfig);
            _isInitialized = true;
        }

        public static async Task<string> GetTokenAsync()
        {
            // PAT takes priority — allows headless/batch use without interactive OAuth.
            string pat = PersonalAccessToken.Load();
            if (!string.IsNullOrEmpty(pat))
                return pat;

            if (!_isInitialized)
                await InitializeAsync();

            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
                return "sandbox-mode";

            if (_authType.Equals("JWT", StringComparison.OrdinalIgnoreCase))
                return await JwtAuth.GetTokenAsync();

            return await MsalAuth.GetTokenAsync();
        }

        // Silent-only sync wrapper — never triggers interactive login (browser/form).
        // Returns null when no cached credentials exist. Use for background operations
        // (auto-import, idle checks) where showing UI would be unexpected.
        public static string GetAuthTokenSilentlyOrNull()
        {
            try
            {
                return Task.Run(async () =>
                {
                    string pat = PersonalAccessToken.Load();
                    if (!string.IsNullOrEmpty(pat)) return pat;

                    if (!_isInitialized)
                        await InitializeAsync();

                    if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
                        return "sandbox-mode";

                    if (_authType.Equals("JWT", StringComparison.OrdinalIgnoreCase))
                        return await JwtAuth.GetTokenSilentAsync();

                    return await MsalAuth.GetTokenSilentAsync();
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Info($"Silent auth check failed: {ex.Message}");
                return null;
            }
        }

        // Sync wrapper — safe to call from non-async Revit event handlers.
        public static string GetAuthTokenSafely()
        {
            try
            {
                return Task.Run(async () => await GetTokenAsync())
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Info($"Failed to retrieve auth token: {ex.Message}");
                return null;
            }
        }

        // Returns "Token" when a PAT is stored (Django TokenAuthentication),
        // "Bearer" for all OAuth2/JWT/sandbox flows. Pair with GetAuthTokenSafely()
        // to build the Authorization header.
        public static string GetAuthSchemeSafely()
        {
            return HasPersonalApiToken() ? "Token" : "Bearer";
        }

        // ── PAT facade (kept on AuthService so callers don't need to know about
        //    the PersonalAccessToken helper class) ──────────────────────────────

        public static void StorePersonalApiToken(string token) => PersonalAccessToken.Store(token);
        public static bool HasPersonalApiToken() => PersonalAccessToken.Exists();
        public static void ClearPersonalApiToken() => PersonalAccessToken.Clear();

        public static void Reset()
        {
            _isInitialized = false;
            _authType = "None";
            MsalAuth.Reset();
            JwtAuth.Reset();
            // PAT is intentionally not cleared on Reset — it persists across server URL changes.
        }
    }
}
