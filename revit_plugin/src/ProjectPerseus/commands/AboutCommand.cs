using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            string revitVersion = commandData.Application.Application.VersionNumber;

            TaskDialog dialog = new TaskDialog("About Project Perseus");
            dialog.MainInstruction = "Project Perseus";
            string channel = pluginVersion.EndsWith(".0") ? "stable release" : "dev build"; // x.y.z.0 = stable (bump-version.ps1)
            dialog.MainContent = $"Plugin version:  {pluginVersion} ({channel})\nRevit version:   {revitVersion}";
            dialog.Show();

            return Result.Succeeded;
        }
    }
}
