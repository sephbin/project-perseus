using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

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

                // Enrich with live Revit values so the review dialog can show current vs new.
                PendingEditsApplier.EnrichWithRevitValues(edits, doc);

                using (var form = new PendingEditsReviewForm(edits))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                        return Result.Cancelled;

                    var selected = form.SelectedEdits;
                    Log.Info($"PullWebEditsCommand: user selected {selected.Count} of {edits.Count} edits to apply.");

                    var result = PendingEditsApplier.Apply(doc, docGuid, selected);
                    string msg = $"Applied {result.Applied} of {result.Total} selected edit(s).";
                    if (result.Skipped > 0)
                        msg += $"\n{result.Skipped} skipped (parameter not found, read-only, or owned by another user).";

                    Log.Info($"PullWebEditsCommand: {msg}");
                    TaskDialog.Show("Perseus — Web Edits", msg);
                }

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
