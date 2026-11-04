using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.sync;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Fallback for elements Django has seen without an architectural ID (created by a user without Perseus, or
    /// skipped earlier because someone else had them checked out). Django queues them as "assign_id" pending
    /// edits; each time one of them is editable here the ID rules allocate its number. Requests for elements still
    /// owned by someone else stay queued for a later sync. Fulfil must run on the main thread, outside document events;
    /// it runs just before a sync (see IdAutoAssignController.RunPreSyncFill) so any ownership it takes is released by
    /// that same sync.
    /// </summary>
    internal static class IdGapFiller
    {
        /// <summary>
        /// Fetches Django's "assign_id" requests and returns the ones that can take an ID in this model, keyed by
        /// ElementId. Requests that never can (annotations, worksets, deleted elements, other categories) are
        /// dismissed on the server so they stop coming back. Makes no model change, so it is safe in the pre-sync hook.
        /// </summary>
        internal static Dictionary<long, PendingEditItem> Collect(Document doc, string docGuid, IdRulesDto rules)
        {
            var byId = new Dictionary<long, PendingEditItem>();
            var requests = PendingEditsApplier.FetchAssignIdRequests(docGuid);
            if (requests.Count == 0) return byId;

            var dismiss = new List<int>();
            foreach (var req in requests)
            {
                var el = doc.GetElement(req.ElementUniqueId);
                if (el == null || !IdAssigner.IsEligible(el) || !IdAssigner.IsApplicable(rules, el)) dismiss.Add(req.Id);
                else byId[el.Id.GetIdValue()] = req;
            }
            if (dismiss.Count > 0) PendingEditsApplier.MarkApplied(docGuid, dismiss, "dismissed");
            Log.Info($"[IdGapFiller] {requests.Count} server request(s), {dismiss.Count} dismissed (not fillable), {byId.Count} fillable");
            return byId;
        }

        internal static void Fulfil(Document doc, string docGuid, IdRulesDto rules)
        {
            var byId = Collect(doc, docGuid, rules);
            if (byId.Count == 0) return;

            var run = IdAssigner.Run(doc, docGuid, rules, byId.Keys.ToList());
            Log.Info($"[IdGapFiller] {byId.Count} attempted, assigned {run.Assigned}, not owned {run.SkippedNotOwned}");
            if (run.RolledBack || run.Errors > 0) return;

            // Done once this user holds the element (we wrote it) or the model isn't workshared. Anything else is
            // still unfilled and stays queued.
            var done = new List<int>();
            foreach (var kv in byId)
            {
                bool filled = !doc.IsWorkshared ||
                    WorksharingUtils.GetCheckoutStatus(doc, RevitExtensions.CreateId(kv.Key)) == CheckoutStatus.OwnedByCurrentUser;
                if (filled) done.Add(kv.Value.Id);
            }
            if (done.Count > 0) PendingEditsApplier.MarkApplied(docGuid, done);
        }
    }
}
