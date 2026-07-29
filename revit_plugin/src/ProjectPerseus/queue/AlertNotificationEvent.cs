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

            // Compile all alerts into one dialog per source (title).
            foreach (var group in alerts.GroupBy(a => a.Title ?? "Perseus"))
            {
                var lines = string.Join("\n", group.Select(a => $"• {a.Body}"));
                new TaskDialog("Perseus")
                {
                    MainInstruction = group.Key,
                    MainContent     = lines,
                    CommonButtons   = TaskDialogCommonButtons.Ok,
                }.Show();
            }
        }

        public string GetName() => "AlertNotificationEvent";
    }
}
