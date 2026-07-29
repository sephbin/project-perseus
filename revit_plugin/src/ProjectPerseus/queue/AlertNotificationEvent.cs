using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.queue
{
    internal class AlertNotificationEvent : IExternalEventHandler
    {
        private ExternalEvent _event;

        internal void SetEvent(ExternalEvent ev) { _event = ev; }

        public void Execute(UIApplication app)
        {
            AlertPoller.IsShowingDialog = true;
            try
            {
                var alerts = AlertPoller.Drain();
                if (alerts.Count == 0) return;

                using (var form = new ui.AlertsReviewForm(alerts))
                    form.ShowDialog();
            }
            catch (Exception ex)
            {
                Log.Warn($"[AlertNotificationEvent] {ex.Message}");
            }
            finally
            {
                AlertPoller.IsShowingDialog = false;
                if (AlertPoller.HasPending && _event != null)
                    _event.Raise();
            }
        }

        public string GetName() => "AlertNotificationEvent";
    }
}
