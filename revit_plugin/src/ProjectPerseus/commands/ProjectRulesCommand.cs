using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.ui;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ProjectRulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log.Info("[ProjectRulesCommand] no active document.");
                    return Result.Failed;
                }

                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Project Rules",
                        "Please configure the server URL in Settings before viewing project rules.");
                    return Result.Failed;
                }

                string docGuid = ModelGuidStorage.GetOrCreate(doc);
                Log.Info($"[ProjectRulesCommand] opening rules form for {docGuid}");

                using (var form = new ProjectRulesForm(docGuid, baseUrl))
                    form.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"[ProjectRulesCommand] failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
