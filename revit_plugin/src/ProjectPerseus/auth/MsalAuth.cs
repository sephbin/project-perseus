using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Newtonsoft.Json.Linq;

namespace ProjectPerseus.auth
{
    // Microsoft Entra ID (MSAL) provider. Initialized lazily from auth-config returned by
    // the Django backend (tenantId, clientId, scopes). Tokens are cached on disk by
    // MsalCacheHelper, scoped to the current Windows user, keyed by clientId.
    internal static class MsalAuth
    {
        private static IPublicClientApplication _msalApp;
        private static string[] _msalScopes;

        public static async Task InitializeAsync(JObject authConfig)
        {
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
        }

        public static async Task<string> GetTokenAsync()
        {
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

        public static void Reset()
        {
            _msalApp = null;
            _msalScopes = null;
        }

        // Run MSAL interactive auth on a dedicated STA thread.
        //
        // MSAL's system-browser flow uses Process.Start + a loopback TCP listener. On a
        // thread-pool (MTA) thread inside Revit this can conflict with Revit's COM/shell
        // environment and crash the process. Running it on a clean STA thread with no
        // SynchronizationContext isolates it completely. A TaskCompletionSource lets the
        // caller await the result without blocking any thread pool threads.
        private static Task<AuthenticationResult> AcquireTokenInteractiveOnStaAsync()
        {
            var tcs = new TaskCompletionSource<AuthenticationResult>();

            var sta = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(null);
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
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
            sta.IsBackground = true;
            sta.Start();

            return tcs.Task;
        }
    }
}
