using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.queue
{
    /// <summary>
    /// Runs a Perseus-triggered Sync with Central in its own ExternalEvent, after the event that wrote to the model
    /// (IDs, web edits) has returned and Revit has idled for a moment. Writing and then calling SynchronizeWithCentral
    /// inside the same API call crashed Revit natively while merging central ("NOBLE safe merging secondary data",
    /// EssentialsDB access violation, 2026-11-25): the sync started before Revit had finished with the transaction
    /// just committed. (That session also ran a Rhino.Inside script later shown to crash Revit on its own, so the cause
    /// is uncertain; writing and syncing in separate API calls is still the safer pattern.)
    /// Used by IdPreSyncFixEvent, AutoSyncEvent and PendingEditsResyncEvent.
    /// One request at a time; a newer one replaces a pending one.
    /// </summary>
    public class DeferredSyncEvent : IExternalEventHandler
    {
        private const int SettleMs = 750;
        private const int GiveUpSeconds = 60;

        internal class Request
        {
            public Document Doc;
            public string What;                                  // for the log
            public Func<SynchronizeWithCentralOptions> Options;  // built when the sync starts
            public Action Before;                                // flags to set right before the sync (main thread)
            public Action After;                                 // always runs once the sync call returns or throws
            public Action<Exception> Failed;                     // sync could not run / threw (main thread)
            internal DateTime NotBefore;
            internal DateTime GiveUpAt;
        }

        private static readonly object _lock = new object();
        private static ExternalEvent _event;
        private static Request _pending;

        /// <summary>Called once from SyncOrchestrator.Subscribe (a valid API context).</summary>
        internal static void Initialize()
        {
            if (_event == null) _event = ExternalEvent.Create(new DeferredSyncEvent());
        }

        /// <summary>Queues the sync. False if it could not be queued (Failed has been called).</summary>
        internal static bool Schedule(Request request)
        {
            request.NotBefore = DateTime.UtcNow.AddMilliseconds(SettleMs);
            request.GiveUpAt = DateTime.UtcNow.AddSeconds(GiveUpSeconds);
            lock (_lock) _pending = request;
            var raised = _event?.Raise() ?? ExternalEventRequest.Denied;
            if (raised == ExternalEventRequest.Accepted || raised == ExternalEventRequest.Pending) return true;

            lock (_lock) if (_pending == request) _pending = null;
            Log.Warn($"[DeferredSync] could not queue the {request.What} re-sync ({raised})");
            request.Failed?.Invoke(new InvalidOperationException("Revit did not accept the request to restart the sync."));
            return false;
        }

        public void Execute(UIApplication app)
        {
            Request r;
            lock (_lock) r = _pending;
            if (r == null) return;

            bool docOk = r.Doc != null && r.Doc.IsValidObject;
            bool waiting = docOk && (DateTime.UtcNow < r.NotBefore || !DocumentSettle.IsSettled(r.Doc));
            if (waiting && DateTime.UtcNow < r.GiveUpAt)
            {
                _event.Raise(); // let Revit idle a little longer, then try again
                return;
            }

            lock (_lock) if (_pending == r) _pending = null;
            if (!docOk || waiting)
            {
                string why = !docOk ? "the model was closed" : "the model stayed busy";
                Log.Warn($"[DeferredSync] {r.What} re-sync skipped: {why}");
                r.Failed?.Invoke(new InvalidOperationException($"Perseus could not restart the sync because {why}."));
                return;
            }

            try
            {
                r.Before?.Invoke();
                Log.Info($"[DeferredSync] starting the {r.What} re-sync of '{r.Doc.Title}'");
                r.Doc.SynchronizeWithCentral(new TransactWithCentralOptions(), r.Options());
                Log.Info($"[DeferredSync] {r.What} re-sync returned");
            }
            catch (Exception ex)
            {
                Log.Error($"[DeferredSync] {r.What} re-sync failed: {ex.Message}");
                try { r.Failed?.Invoke(ex); } catch (Exception inner) { Log.Warn($"[DeferredSync] failure handler threw: {inner.Message}"); }
            }
            finally
            {
                try { r.After?.Invoke(); } catch (Exception ex) { Log.Warn($"[DeferredSync] after handler threw: {ex.Message}"); }
            }
        }

        public string GetName() => "Perseus Deferred Sync";
    }
}
