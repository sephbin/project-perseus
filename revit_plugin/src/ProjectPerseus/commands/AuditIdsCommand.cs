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
                    TaskDialog.Show("Perseus — Audit IDs", "Open an editable project model to audit its IDs.");
                    return Result.Cancelled;
                }

                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Audit IDs",
                        "Please configure the server URL in Settings before auditing IDs.");
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
                IdAutoAssignController.Reset(docGuid);
                IdAssignResult result = IdAssigner.Run(doc, docGuid, rules, null);

                Log.Info($"[AuditIdsCommand] audit complete for {docGuid}: {result.Summary().Replace("\n", " | ")}");
                TaskDialog.Show("Perseus — Audit IDs",
                    (created > 0 ? $"Parameters created or extended: {created}\n" : "") +
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
    }
}
