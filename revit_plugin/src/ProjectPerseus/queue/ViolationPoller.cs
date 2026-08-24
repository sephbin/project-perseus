using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.queue
{
    // Heartbeat poller that fetches current violations from Django every 60 s per open source.
    // Pattern mirrors PresencePoller: static class, HashSet of GUIDs, EnsureRunning/Stop,
    // AddSource/RemoveSource called from OnDocumentOpened/OnDocumentClosing.
    internal static class ViolationPoller
    {
        private static readonly HashSet<string> _activeGuids = new HashSet<string>();
        private static readonly object _lock = new object();
        private static CancellationTokenSource _cts;

        // Set once at Subscribe() time by SyncOrchestrator.
        internal static Autodesk.Revit.UI.ExternalEvent HighlightEvent;

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
                Log.Info("[ViolationPoller] Started.");
                PollAll();   // immediate first tick on document open
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(60000, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        PollAll();
                }
                Log.Info("[ViolationPoller] Stopped.");
            }, token);
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

        private static void PollAll()
        {
            string[] guids;
            lock (_lock)
            {
                guids = new string[_activeGuids.Count];
                _activeGuids.CopyTo(guids);
            }

            var all = new List<ViolationHighlightDto>();
            string baseUrl = Config.Instance.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return;

            foreach (var guid in guids)
            {
                try
                {
                    all.AddRange(web.ProjectPerseusWeb.GetViolations(baseUrl, guid));
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ViolationPoller] fetch failed for {guid}: {ex.Message}");
                }
            }

            ViolationHighlightEvent.SetPending(all);
            HighlightEvent?.Raise();
        }
    }
}
