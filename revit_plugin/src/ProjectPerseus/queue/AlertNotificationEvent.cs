using System.Collections.Generic;
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

            foreach (var alert in alerts)
            {
                var dlg = new TaskDialog("Perseus Alert")
                {
                    MainInstruction = alert.Title,
                    MainContent     = alert.Body,
                    CommonButtons   = TaskDialogCommonButtons.Ok,
                };
                dlg.Show();
            }
        }

        public string GetName() => "AlertNotificationEvent";
    }
}
