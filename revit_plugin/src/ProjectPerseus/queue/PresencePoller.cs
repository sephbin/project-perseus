using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.queue
{
    // Fires a heartbeat to Syncboat for every open source document every 60 s so the
    // web queue can display who currently has the model open in Revit. Entries not
    // refreshed within 180 s (3 × poll interval) are dropped from the active-users list.
    internal static class PresencePoller
    {
        private static readonly HashSet<string> _activeGuids = new HashSet<string>();
        private static readonly object _lock = new object();
        private static CancellationTokenSource _cts;
        private static readonly string _username = Environment.UserName.ToLower();

        internal static void AddSource(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            lock (_lock) { _activeGuids.Add(guid); }
            EnsureRunning();
        }

        internal static void RemoveSource(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            bool empty;
            lock (_lock)
            {
                _activeGuids.Remove(guid);
                empty = _activeGuids.Count == 0;
            }
            if (empty) Stop();
        }

        private static void EnsureRunning()
        {
            lock (_lock)
            {
                if (_cts != null) return;
                _cts = new CancellationTokenSource();
            }
            var token = _cts.Token;
            Task.Run(async () =>
            {
                Log.Info("[PresencePoller] Started.");
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(60000, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;
                    SendHeartbeats();
                }
                Log.Info("[PresencePoller] Stopped.");
            }, token);

            // Fire an immediate heartbeat so the web queue shows the user right away
            // rather than waiting up to 60 s for the first scheduled tick.
            Task.Run(() => SendHeartbeats());
        }

        private static void Stop()
        {
            CancellationTokenSource old;
            lock (_lock)
            {
                old = _cts;
                _cts = null;
            }
            old?.Cancel();
            old?.Dispose();
        }

        private static void SendHeartbeats()
        {
            string[] guids;
            lock (_lock) { guids = new string[_activeGuids.Count]; _activeGuids.CopyTo(guids); }

            var baseUrl = Config.Instance.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return;

            var syncboatRoot = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
            var payload = JsonConvert.SerializeObject(new { username = _username });

            foreach (var guid in guids)
            {
                try
                {
                    var endpoint = $"{syncboatRoot}/syncboat/api/v2/source/{guid}/heartbeat/";
                    WebHelper.Post(endpoint, null, payload);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[PresencePoller] Heartbeat failed for {guid}: {ex.Message}");
                }
            }
        }
    }
}
