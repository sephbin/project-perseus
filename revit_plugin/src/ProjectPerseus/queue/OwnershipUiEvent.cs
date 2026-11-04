using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.queue
{
    /// <summary>
    /// Runs Revit API work requested by the ownership UI (the modeless OwnershipForm and the status-bar badge),
    /// which live outside any API context. Callers queue an action with <see cref="Run"/>; Revit executes it on
    /// the main thread when the ExternalEvent fires.
    /// </summary>
    public class OwnershipUiEvent : IExternalEventHandler
    {
        private static ExternalEvent _event;
        private static readonly ConcurrentQueue<Action<UIApplication>> _actions = new ConcurrentQueue<Action<UIApplication>>();

        // Must be called from a valid API context (OnStartup).
        internal static void Create()
        {
            if (_event == null) _event = ExternalEvent.Create(new OwnershipUiEvent());
        }

        internal static void Run(Action<UIApplication> action)
        {
            if (action == null || _event == null) return;
            _actions.Enqueue(action);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_actions.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex) { Log.Warn($"[OwnershipUiEvent] action failed: {ex.Message}"); }
            }
        }

        public string GetName() => "Perseus Ownership UI";
    }
}
