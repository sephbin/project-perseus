using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using ProjectPerseus.models;

namespace ProjectPerseus.queue
{
    internal class AlertNotificationEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var alerts = new List<AlertDto>(AlertPoller.PendingAlerts);
            if (alerts.Count == 0) return;
            AlertPoller.PendingAlerts.Clear();

            // One scrollable form per source (grouped by title).
            foreach (var group in alerts.GroupBy(a => a.Title ?? "Perseus"))
            {
                using (var form = new ui.AlertsReviewForm(group.Key, group.ToList()))
                    form.ShowDialog();
            }
        }

        public string GetName() => "AlertNotificationEvent";
    }
}
