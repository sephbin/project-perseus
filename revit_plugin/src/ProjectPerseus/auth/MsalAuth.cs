using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Newtonsoft.Json.Linq;

using ProjectPerseus.logging;
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

            Log.Info($"Auth mode: EntraID. Tenant: {tenantId}");
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
                    Log.Info($"Interactive MSAL login failed: {ex.Message}");
                    return null;
                }
            }
            return result.AccessToken;
        }

        // Silent-only variant — returns null rather than triggering interactive login.
        // Used by background/automatic operations that must never show UI.
        public static async Task<string> GetTokenSilentAsync()
        {
            if (_msalApp == null) return null;
            try
            {
                var accounts = await _msalApp.GetAccountsAsync();
                var result = await _msalApp
                    .AcquireTokenSilent(_msalScopes, accounts.FirstOrDefault())
                    .ExecuteAsync();
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                return null; // no cached token — caller should let user authenticate normally
            }
            catch (Exception ex)
            {
                Log.Info($"MSAL silent token attempt failed: {ex.Message}");
                return null;
            }
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
                                    "<html><head><script>window.onload=function(){window.close();}</script>" +
                                    "<style>body{font-family:sans-serif;display:flex;align-items:center;" +
                                    "justify-content:center;height:100vh;margin:0;background:#f3f3f3;}" +
                                    "div{text-align:center;padding:2em;background:#fff;border-radius:8px;" +
                                    "box-shadow:0 2px 8px rgba(0,0,0,.15);}" +
                                    "h2{color:#107C10;margin:0 0 .5em;}p{color:#555;margin:0;}</style></head>" +
                                    "<body><div><h2>&#10003; Authentication complete</h2>" +
                                    "<p>You may close this window and return to Revit.</p></div></body></html>",
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
