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
    internal class AlertPoller
    {
        private CancellationTokenSource _cts;
        private readonly Autodesk.Revit.UI.ExternalEvent _alertEvent;
        private readonly Config _config;
        private readonly Func<string> _getToken;

        // Thread-safe alert queue: background poller enqueues; main thread drains.
        private static readonly object _lock = new object();
        private static readonly List<AlertDto> _pending = new List<AlertDto>();

        // Set true by AlertNotificationEvent while a dialog is visible so the
        // poller accumulates without raising a second event unnecessarily.
        internal static volatile bool IsShowingDialog = false;

        // Live form reference — set by AlertNotificationEvent while dialog is open.
        // Used by Enqueue to inject alerts directly rather than waiting for re-raise.
        internal static ui.AlertsReviewForm ActiveForm = null;

        internal static void Enqueue(IEnumerable<AlertDto> alerts)
        {
            lock (_lock) { _pending.AddRange(alerts); }

            // If a dialog is already open, push new alerts directly into it.
            var form = ActiveForm;
            if (IsShowingDialog && form != null)
            {
                try { form.BeginInvoke(new Action(form.DrainAndAppend)); }
                catch { /* form was disposed between check and BeginInvoke */ }
            }
        }

        internal static List<AlertDto> Drain()
        {
            lock (_lock)
            {
                var copy = new List<AlertDto>(_pending);
                _pending.Clear();
                return copy;
            }
        }

        internal static bool HasPending
        {
            get { lock (_lock) { return _pending.Count > 0; } }
        }

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
                            Enqueue(alerts);
                            // Raise the ExternalEvent only when no dialog is currently open.
                            // If a dialog IS open, Enqueue() already pushed via BeginInvoke.
                            if (!IsShowingDialog)
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
