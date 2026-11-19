using System;
using System.Linq;
using System.Text;
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
    // Re-applies the project's ID rules to every element in the model, as if each had just been
    // created or edited. Catches copies and edits made outside Perseus.
    [Transaction(TransactionMode.Manual)]
    public class AuditIdsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Log.Info("[AuditIdsCommand] no active document.");
                    return Result.Failed;
                }
                if (doc.IsFamilyDocument || doc.IsReadOnly)
                {
                    TaskDialog.Show("Perseus — Audit Auto Parameters", "Open an editable project model to audit its automatic parameters.");
                    return Result.Cancelled;
                }

                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Audit Auto Parameters",
                        "Please configure the server URL in Settings before auditing automatic parameters.");
                    return Result.Failed;
                }

                string docGuid = ModelGuidStorage.GetOrCreate(doc);
                IdRulesDto rules = IdRulesCache.LoadNow(docGuid, baseUrl);
                string serverNote = "";
                if (rules == null)
                {
                    rules = IdRulesCache.Get(docGuid) ?? new IdRulesDto();
                    serverNote = "\n\nThe server could not be reached, so " +
                                 (rules.Rules.Count > 0 ? "the last known rules were used." : "only Perseus_ID was applied.");
                }

                int created = SharedParameterManager.Ensure(doc, rules);
                string lockNote = SwitchLocks(doc, rules);
                IdAutoAssignController.Reset(docGuid);
                IdAssignResult result = IdAssigner.Run(doc, docGuid, rules, null);

                Log.Info($"[AuditIdsCommand] audit complete for {docGuid}: {result.Summary().Replace("\n", " | ")}");
                TaskDialog.Show("Perseus — Audit Auto Parameters",
                    (created > 0 ? $"Parameters created or extended: {created}\n" : "") + lockNote +
                    result.Summary() + serverNote);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"[AuditIdsCommand] failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        // Rules whose Read-only option no longer matches their parameter: Revit only sets that flag when a parameter is
        // created, so ask before replacing the parameter (new GUID, values copied). Returns lines for the summary.
        private static string SwitchLocks(Document doc, IdRulesDto rules)
        {
            var mismatches = SharedParameterManager.LockMismatches(doc, rules);
            if (mismatches.Count == 0) return "";
            string list = string.Join("\n", mismatches.Select(m => $"  • {m.Name}: {(m.WantReadOnly ? "lock (read-only)" : "unlock (editable)")}"));
            var dlg = new TaskDialog("Perseus — Lock parameters")
            {
                MainInstruction = "Change which parameters users can edit?",
                MainContent = list + "\n\nRevit only sets this when a parameter is created, so Perseus replaces each one with a new " +
                              "parameter of the same name and copies every value across (one undo step).\n\n" +
                              "Schedules, view filters and tags that use the old parameter lose it and must be pointed at the new one. " +
                              "Every element holding a value must be available: not checked out by someone else, and up to date with central.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            if (dlg.Show() != TaskDialogResult.Yes) return "Lock changes not made (Audit again to be asked again).\n";

            var lines = new StringBuilder();
            foreach (var m in mismatches)
            {
                string err = SharedParameterManager.SwitchLock(doc, m.Name, m.WantReadOnly, out int copied, out int failed);
                lines.Append(err == null
                    ? $"{m.Name} {(m.WantReadOnly ? "locked" : "unlocked")}: {copied} value(s) copied" + (failed > 0 ? $", {failed} failed" : "") + "\n"
                    : $"{m.Name} not changed: {err}\n");
            }
            return lines.ToString();
        }
    }
}
