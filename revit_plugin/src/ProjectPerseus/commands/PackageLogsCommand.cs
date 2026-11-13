using System;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    /// <summary>Ribbon: Button_PackageLogs. Zips the Perseus logs into Downloads and shows the file in Explorer.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class PackageLogsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string plugin = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
                var r = LogPackager.Package(commandData.Application.Application.VersionNumber, plugin);

                try { Process.Start("explorer.exe", $"/select,\"{r.ZipPath}\""); }
                catch (Exception e) { Log.Info($"[LogPackager] could not open Explorer: {e.Message}"); }

                TaskDialog.Show("Perseus logs",
                    $"Packaged {r.Files} log file(s) into:\n\n{r.ZipPath}\n\nSend this file to the Perseus team.");
                return Result.Succeeded;
            }
            catch (Exception e)
            {
                Log.Exception(e);
                TaskDialog.Show("Perseus logs", "Could not package the logs:\n\n" + e.Message);
                return Result.Failed;
            }
        }
    }
}
