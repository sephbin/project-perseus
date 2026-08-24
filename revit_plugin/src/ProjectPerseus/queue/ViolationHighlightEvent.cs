using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.violations;

namespace ProjectPerseus.queue
{
    internal class ViolationHighlightEvent : IExternalEventHandler
    {
        private static readonly object _lock = new object();
        private static List<ViolationHighlightDto> _pending = new List<ViolationHighlightDto>();

        internal static void SetPending(List<ViolationHighlightDto> violations)
        {
            lock (_lock)
            {
                _pending = violations ?? new List<ViolationHighlightDto>();
            }
        }

        public void Execute(UIApplication app)
        {
            List<ViolationHighlightDto> violations;
            lock (_lock) { violations = _pending; }

            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return;
                ViolationHighlightController.Update(violations, doc);
                if (ViolationHighlightServer.Instance.IsEnabled)
                    app.ActiveUIDocument.RefreshActiveView();
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationHighlightEvent] {ex.Message}");
            }
        }

        public string GetName() => "Perseus Violation Highlight Update";
    }
}
