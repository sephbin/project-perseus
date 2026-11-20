using System;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.commands
{
    /// <summary>
    /// Ribbon: Button_WebHub. Opens the active model's hub page on the Perseus web app
    /// (django-perseus source_hub_view, <c>{BaseUrl}/source/&lt;model guid&gt;/</c>): every web feature for the
    /// model and its project in one place. Without an open model it opens the web app's home page.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenWebHubCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string baseUrl = Config.Instance.BaseUrl?.TrimEnd('/');
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Web", "Please configure the server URL in Settings first.");
                    return Result.Failed;
                }

                string url = baseUrl + "/";
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc != null && !doc.IsFamilyDocument)
                {
                    string docGuid = ModelGuidStorage.GetOrCreate(doc);
                    if (!string.IsNullOrEmpty(docGuid))
                        url = $"{baseUrl}/source/{docGuid}/";
                }

                Log.Info($"[OpenWebHubCommand] opening {url}");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"[OpenWebHubCommand] failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
