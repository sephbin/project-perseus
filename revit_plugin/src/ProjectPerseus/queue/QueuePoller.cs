using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.models;

namespace ProjectPerseus.queue
{
    // Background poller that watches the syncboat queue every 5s. When the current user
    // reaches the front, raises the supplied ExternalEvent so AutoSyncEvent fires on the
    // Revit main thread. The v2 queue endpoint is LOGIN_EXEMPT, so no auth token is needed.
    public class QueuePoller
    {
        private CancellationTokenSource _cancellationTokenSource;
        private readonly Autodesk.Revit.UI.ExternalEvent _syncEvent;
        private readonly Config _config;
        private readonly string _username;

        public QueuePoller(Autodesk.Revit.UI.ExternalEvent syncEvent)
        {
            _syncEvent = syncEvent;
            _config = Config.Instance;
            _username = Environment.UserName;
        }

        public void StartPolling(string currentDocGuid)
        {
            Stop();

            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            Utl.WriteLog($"Auto-Sync Poller started for {currentDocGuid}. Waiting for our turn...");

            Task.Run(async () =>
            {
                var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/../syncboat/api/v2/source/{currentDocGuid}/queue/";

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        string response = Utl.WebHelper.Get(endpoint, null, null);
                        var queueStatus = JsonConvert.DeserializeObject<SyncQueueResponse>(response);
                        List<string> users = queueStatus?.Queue ?? new List<string>();

                        // Queue may return full UPN (e.g. Andrew.Butler@cox.com.au) — strip @domain before comparing.
                        string frontUser = users.Count > 0 && users[0].Contains("@")
                            ? users[0].Split('@')[0]
                            : (users.Count > 0 ? users[0] : "");

                        if (users.Count > 0 && frontUser.Equals(_username, StringComparison.OrdinalIgnoreCase))
                        {
                            Utl.WriteLog("We are first in the queue! Triggering Revit Sync...");
                            _syncEvent.Raise();
                            Stop();
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Utl.WriteLog($"Poller error: {ex.Message}");
                    }

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
