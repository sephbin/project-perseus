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
                {
                    AlertPoller.ActiveForm = form;
                    form.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[AlertNotificationEvent] {ex.Message}");
            }
            finally
            {
                // Clear ActiveForm before clearing IsShowingDialog so a racing poller
                // thread that sees IsShowingDialog=true doesn't BeginInvoke a disposed form.
                AlertPoller.ActiveForm = null;
                AlertPoller.IsShowingDialog = false;
                // Re-raise for any alerts that slipped in between form.Close() and ActiveForm=null.
                if (AlertPoller.HasPending && _event != null)
                    _event.Raise();
            }
        }

        public string GetName() => "AlertNotificationEvent";
    }
}
