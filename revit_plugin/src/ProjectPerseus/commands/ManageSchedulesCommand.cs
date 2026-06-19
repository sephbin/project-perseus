using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.ui;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class ManageSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            List<KeyScheduleConfig> existing = KeyScheduleStorage.Load(doc);

            using (var form = new ManageSchedulesForm(existing))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;

                KeyScheduleStorage.Save(doc, form.Result ?? new List<KeyScheduleConfig>());
            }

            TaskDialog.Show("Perseus: Key Schedules",
                "Key schedule mappings saved to this project file.");
            return Result.Succeeded;
        }
    }
}
