using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;

namespace ProjectPerseus.commands
{
    // Renumbers every ID rule parameter from 1 within each counter group (e.g. each level and category), in
    // the rule's numbering order (left to right, top to bottom, ...). Unlike Audit IDs, which keeps valid
    // numbers, this replaces existing ones; it is meant for a deliberate milestone, so it asks first.
    [Transaction(TransactionMode.Manual)]
    public class RenumberIdsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log.Info("[RenumberIdsCommand] no active document.");
                    return Result.Failed;
                }
                if (doc.IsFamilyDocument || doc.IsReadOnly)
                {
                    TaskDialog.Show("Perseus — Renumber IDs", "Open an editable project model to renumber its IDs.");
                    return Result.Cancelled;
                }

                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Renumber IDs",
                        "Please configure the server URL in Settings before renumbering IDs.");
                    return Result.Failed;
                }

                string docGuid = ModelGuidStorage.GetOrCreate(doc);
                IdRulesDto rules = IdRulesCache.LoadNow(docGuid, baseUrl);
                if (rules == null)
                {
                    TaskDialog.Show("Perseus — Renumber IDs",
                        "The server could not be reached, so the ID rules could not be loaded. Nothing was changed.");
                    return Result.Failed;
                }

                var confirm = new TaskDialog("Perseus — Renumber IDs")
                {
                    MainInstruction = "Renumber every ID in this model?",
                    MainContent =
                        "Each ID rule is renumbered from 1 within each counter group, in the order set for that rule " +
                        "on the server. Existing numbers are replaced, so anything that refers to the old IDs " +
                        "(markups, schedules exported earlier) will no longer match.\n\n" +
                        "Use this for a deliberate milestone. Audit IDs is the safe everyday check.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                int created = SharedParameterManager.Ensure(doc, rules);
                IdAutoAssignController.Reset(docGuid);
                IdAssignResult result = IdAssigner.Run(doc, docGuid, rules, null, renumber: true);

                Log.Info($"[RenumberIdsCommand] renumber complete for {docGuid}: {result.Summary().Replace("\n", " | ")}");
                TaskDialog.Show("Perseus — Renumber IDs",
                    (created > 0 ? $"Parameters created or extended: {created}\n" : "") + result.Summary());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"[RenumberIdsCommand] failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
