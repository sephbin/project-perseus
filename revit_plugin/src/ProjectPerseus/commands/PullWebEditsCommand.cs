using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.sync;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class PullWebEditsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log.Info("PullWebEditsCommand: no active document.");
                    return Result.Failed;
                }

                var docGuid = ModelGuidStorage.GetOrCreate(doc);
                Log.Info($"PullWebEditsCommand: fetching pending edits for {docGuid}");

                var edits = PendingEditsApplier.Fetch(docGuid);
                if (edits.Count == 0)
                {
                    TaskDialog.Show("Perseus — Web Edits", "No pending web edits found for this model.");
                    return Result.Succeeded;
                }

                var result  = PendingEditsApplier.Apply(doc, docGuid, edits);
                string msg  = $"Applied {result.Applied} of {result.Total} pending web edit(s).";
                if (result.Skipped > 0)
                    msg += $"\n{result.Skipped} skipped (parameter not found, read-only, or owned by another user).";

                Log.Info($"PullWebEditsCommand: {msg}");
                TaskDialog.Show("Perseus — Web Edits", msg);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"PullWebEditsCommand failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
