using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using ProjectPerseus.models;

namespace ProjectPerseus.queue
{
    public class QueuePoller
    {
        private CancellationTokenSource _cancellationTokenSource;
        private readonly Autodesk.Revit.UI.ExternalEvent _syncEvent;
        private readonly Config _config;
        private readonly string _username;

        // 🔹 Removed docGuid from the constructor
        public QueuePoller(Autodesk.Revit.UI.ExternalEvent syncEvent)
        {
            _syncEvent = syncEvent;
            _config = Config.Instance;
            _username = Environment.UserName;
        }

        // 🔹 Pass currentDocGuid exactly when you start polling so it's always fresh
        public void StartPolling(string currentDocGuid)
        {
            // Stop existing poller if one is running
            Stop();

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            Utl.WriteLog($"Auto-Sync Poller started for {currentDocGuid}. Waiting for our turn...");

            Task.Run(async () =>
            {
                // 🔹 Use the fresh docGuid passed into the method
                var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/../syncboat/getCurrentQueue/{currentDocGuid}/";

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 1. Get the MSAL token safely
                        string authToken = ProjectPerseus.auth.AuthService.GetAuthTokenSafely();
                        if (string.IsNullOrEmpty(authToken))
                        {
                            Utl.WriteLog("Poller aborted: User is not authenticated.");
                            Stop();
                            break;
                        }

                        // 2. Ask Django who is in the queue using HttpClient
                        using (var client = new System.Net.Http.HttpClient())
                        {
                            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

                            string response = await client.GetStringAsync(endpoint);
                            var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(response);
                            List<string> users = queueStatus?.Queue ?? new List<string>();

                            // 3. Check if we are FIRST in the list
                            if (users.Count > 0 && users[0].Equals(_username, StringComparison.OrdinalIgnoreCase))
                            {
                                Utl.WriteLog("We are first in the queue! Triggering Revit Sync...");

                                // 4. WAKE UP REVIT!
                                _syncEvent.Raise();

                                // 5. Stop polling (our job is done)
                                Stop();
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"Poller error: {ex.Message}");
                    }

                    // Wait 5 seconds before checking again (don't spam the server)
                    await Task.Delay(5000, token);
                }
            }, token);
        }

        public void Stop()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                Utl.WriteLog("Auto-Sync Poller stopped.");
            }
        }
    }
}