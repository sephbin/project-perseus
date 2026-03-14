using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using ProjectPerseus.models; // Ensure your SyncQueueResponse is accessible

namespace ProjectPerseus.queue
{
    public class QueuePoller
    {
        private CancellationTokenSource _cancellationTokenSource;
        private readonly Autodesk.Revit.UI.ExternalEvent _syncEvent;
        private readonly Config _config;
        private readonly string _docGuid;
        private readonly string _username;

        // 🔹 Pass the ExternalEvent into the poller when creating it
        public QueuePoller(Autodesk.Revit.UI.ExternalEvent syncEvent, string docGuid)
        {
            _syncEvent = syncEvent;
            _config = Config.Instance;
            _docGuid = docGuid;
            _username = Environment.UserName;
        }

        public void StartPolling()
        {
            // Stop existing poller if one is running
            Stop();

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            Utl.WriteLog("Auto-Sync Poller started. Waiting for our turn...");

            Task.Run(async () =>
            {
                var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/../syncboat/getCurrentQueue/{_docGuid}/";

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 1. Ask Django who is in the queue
                        string response = Utl.WebHelper.Get(endpoint, _config.ApiToken, null);
                        var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(response);
                        List<string> users = queueStatus?.Queue ?? new List<string>();

                        // 2. Check if we are FIRST in the list
                        if (users.Count > 0 && users[0].Equals(_username, StringComparison.OrdinalIgnoreCase))
                        {
                            Utl.WriteLog("We are first in the queue! Triggering Revit Sync...");

                            // 3. WAKE UP REVIT!
                            _syncEvent.Raise();

                            // 4. Stop polling (our job is done)
                            Stop();
                            break;
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