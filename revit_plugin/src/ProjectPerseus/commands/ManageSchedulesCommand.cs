using System.Collections.Generic;
using System.Linq;
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

            // Collect key schedules from the document for the dropdown
            List<string> availableSchedules = GetKeyScheduleNames(doc);

            using (var form = new ManageSchedulesForm(existing, availableSchedules))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;

                KeyScheduleStorage.Save(doc, form.Result ?? new List<KeyScheduleConfig>());
            }

            TaskDialog.Show("Perseus: Key Schedules", "Key schedule mappings saved.");
            return Result.Succeeded;
        }

        private static List<string> GetKeyScheduleNames(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => IsKeySchedule(vs))
                .Select(vs => vs.Name)
                .OrderBy(n => n)
                .ToList();
        }

        private static bool IsKeySchedule(ViewSchedule vs)
        {
            try { return vs.Definition.IsKeySchedule; }
            catch { return false; }
        }
    }
}
