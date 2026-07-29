using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.web;

namespace ProjectPerseus.queue
{
    // Polls /api/source/<guid>/pending-revit-alerts/ every 60 seconds in the background.
    // When undelivered alerts arrive, raises the supplied ExternalEvent so
    // AlertNotificationEvent can show a TaskDialog on the Revit main thread.
    internal class AlertPoller
    {
        private CancellationTokenSource _cts;
        private readonly Autodesk.Revit.UI.ExternalEvent _alertEvent;
        private readonly Config _config;
        private readonly Func<string> _getToken;

        public static List<AlertDto> PendingAlerts { get; private set; } = new List<AlertDto>();

        public AlertPoller(Autodesk.Revit.UI.ExternalEvent alertEvent, Func<string> getToken)
        {
            _alertEvent = alertEvent;
            _config     = Config.Instance;
            _getToken   = getToken;
        }

        public void StartPolling(string docGuid)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Log.Info($"[AlertPoller] Starting for {docGuid}");
            Task.Run(async () =>
            {
                var username = Uri.EscapeDataString(Environment.UserName.ToLower());
                var url = $"{_config.BaseUrl.TrimEnd('/')}/api/source/{docGuid}/pending-revit-alerts/?username={username}";
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(60000, token);
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        var authToken = _getToken();
                        var json = WebHelper.Get(url, authToken, null);
                        var alerts = JsonConvert.DeserializeObject<List<AlertDto>>(json);
                        if (alerts != null && alerts.Count > 0)
                        {
                            PendingAlerts = alerts;
                            _alertEvent.Raise();
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Log.Warn($"[AlertPoller] {ex.Message}"); }
                }
            }, token);
        }

        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
                Log.Info("[AlertPoller] Stopped.");
            }
        }
    }
}
