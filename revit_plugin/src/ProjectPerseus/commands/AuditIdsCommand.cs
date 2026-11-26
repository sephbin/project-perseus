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
                string lockNote = MergeDuplicates(doc, rules) + SwitchLocks(doc, rules);
                IdAutoAssignController.Reset(docGuid);
                IdAssignResult result = IdAssigner.Run(doc, docGuid, rules, null);

                // Collection lists (member values listed on the owner): fresh config, then every owner.
                collections.CollectionsCache.LoadNow(docGuid, baseUrl);
                var lists = collections.CollectionLists.WithLists(doc).Count > 0 ? collections.CollectionLists.Refresh(doc, null) : null;

                Log.Info($"[AuditIdsCommand] audit complete for {docGuid}: {result.Summary().Replace("\n", " | ")}");
                TaskDialog.Show("Perseus — Audit Auto Parameters",
                    (created > 0 ? $"Parameters created or extended: {created}\n" : "") + lockNote +
                    result.Summary() + (lists != null ? Environment.NewLine + lists.Summary() : "") + serverNote);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error($"[AuditIdsCommand] failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        // Rule parameters Perseus created beside the model's own shared parameter of the same name (before it reused
        // existing ones): elements carry two parameters with one name and schedules / family labels read the other one.
        private static string MergeDuplicates(Document doc, IdRulesDto rules)
        {
            var names = rules.Rules.Where(r => r.Enabled).Select(r => r.ParameterName).ToList();
            var dups = SharedParameterManager.Duplicates(doc, names);
            if (dups.Count == 0) return "";
            string list = string.Join("\n", dups.Select(d => $"  • {d.Name}"));
            var dlg = new TaskDialog("Perseus — Duplicate parameters")
            {
                MainInstruction = "Use the model's own shared parameters?",
                MainContent = list + "\n\nThe model (or its families) already has a shared parameter with each of these names, and Perseus " +
                              "added its own copy beside it, so elements carry two parameters with the same name. Families' labels and " +
                              "schedules read the model's one, which Perseus hasn't been filling in.\n\n" +
                              "Perseus can remove its copy, bind the model's parameter to the same categories and copy every value " +
                              "across (one undo step). Every element holding a value must be available: not checked out by someone " +
                              "else, and up to date with central. Rule lock settings don't apply to parameters that aren't Perseus's own.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes,
            };
            if (dlg.Show() != TaskDialogResult.Yes) return "Duplicate parameters kept (Audit again to be asked again).\n";
            var lines = new StringBuilder();
            foreach (var d in dups)
            {
                string err = SharedParameterManager.MergeDuplicate(doc, d, out int copied, out int failed);
                lines.Append(err == null
                    ? $"{d.Name}: now the model's own parameter, {copied} value(s) copied" + (failed > 0 ? $", {failed} failed" : "") + "\n"
                    : $"{d.Name} not merged: {err}\n");
            }
            return lines.ToString();
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
