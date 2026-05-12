using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Newtonsoft.Json.Linq;

namespace ProjectPerseus.auth
{
    public class AuthService
    {
        private static IPublicClientApplication _app;
        private static string[] _scopes;

        // Track the current authentication mode
        private static string _authType = "EntraID";
        private static bool _isInitialized = false;

        public static async Task InitializeAsync()
        {
            var baseUrl = Config.Instance.BaseUrl.TrimEnd('/');
            string configJson = Utl.WebHelper.Get($"{baseUrl}/api/auth-config/", null, null);

            if (string.IsNullOrEmpty(configJson))
                throw new Exception("Could not fetch authentication configuration from server.");

            JObject authConfig = JObject.Parse(configJson);

            // --- 1. THE SANDBOX CHECK ---
            if (authConfig["authType"] != null)
            {
                _authType = authConfig["authType"].ToString();
            }

            // If the server says no auth is required, skip MSAL setup entirely
            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                Utl.WriteLog("Sandbox Mode detected. Bypassing MSAL initialization.");
                _isInitialized = true;
                return;
            }

            // --- 2. STANDARD ENTRA ID SETUP ---
            string clientId = authConfig["clientId"].ToString();
            string tenantId = authConfig["tenantId"].ToString();
            _scopes = authConfig["scopes"].ToObject<string[]>();

            string authority = $"https://login.microsoftonline.com/{tenantId}";

            _app = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(authority)
                .WithDefaultRedirectUri()
                .Build();

            // Bind the cache
            var storageProperties = new StorageCreationPropertiesBuilder($"perseus_cache_{clientId}.bin", MsalCacheHelper.UserRootDirectory)
                .Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties);
            cacheHelper.RegisterCache(_app.UserTokenCache);

            _isInitialized = true;
        }

        public static async Task<string> GetTokenAsync()
        {
            if (!_isInitialized) await InitializeAsync();

            // --- 3. THE SANDBOX TOKEN INTERCEPT ---
            if (_authType.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                // Return the dummy token that Django will recognize
                return "sandbox-mode";
            }

            // --- 4. STANDARD MSAL TOKEN FLOW ---
            var accounts = await _app.GetAccountsAsync();
            AuthenticationResult result;

            try
            {
                result = await _app.AcquireTokenSilent(_scopes, accounts.FirstOrDefault()).ExecuteAsync();
            }
            catch (MsalUiRequiredException)
            {
                try
                {
                    result = await _app.AcquireTokenInteractive(_scopes)
                        .WithUseEmbeddedWebView(false)
                        .ExecuteAsync();
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Interactive login failed/cancelled: {ex.Message}");
                    return null;
                }
            }

            return result.AccessToken;
        }
        public static string GetAuthTokenSafely()
        {
            try
            {
                // Push the async work to a background thread to prevent UI deadlocks
                return System.Threading.Tasks.Task.Run(async () =>
                {
                    return await GetTokenAsync(); // Calls the async method in this same class
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to retrieve auth token safely: {ex.Message}");
                return null;
            }
        }
        public static void Reset()
        {
            _app = null;
            _isInitialized = false;
            _authType = "EntraID";
        }
    }
}