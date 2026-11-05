using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

namespace ProjectPerseus.queue
{
    // Raised by SyncOrchestrator when the pre-sync ID check finds work: elements that need an ID written (other
    // users' new elements, Django's "assign_id" requests) and/or values another user already holds. The sync was
    // cancelled (modifying the model inside DocumentSynchronizingWithCentral races with Revit's central
    // communication), so here, on the main thread, the IDs are written and clashes renumbered, then the sync is
    // re-triggered with the user's own options. Writing just before the sync means the elements Perseus had to
    // check out are released by that same sync instead of staying owned until the user next syncs.
    // IsResyncAfterIdFix skips the ID check on that one pass; the queue check still runs.
    public class IdPreSyncFixEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                SyncStatusToast.Show("Writing element IDs before your sync…");
                IdAutoAssignController.RunPreSyncFill(doc);

                var clashes = IdClaims.EnsureNoClashes(doc);
                if (clashes.Blocking && !AskAboutClashes(app, clashes)) return;
                string note = IdClaims.Describe(clashes);
                if (!string.IsNullOrEmpty(note)) Log.Info("[IdClaims] " + note.Replace("\n", " "));

                Log.Info("Triggering re-sync after the pre-sync ID fill / clash fix.");
                SyncStatusToast.Show("IDs written. Restarting your sync now…" + (string.IsNullOrEmpty(note) ? "" : "\n" + note));
                SyncOrchestrator.IsResyncAfterIdFix = true;
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), SyncOrchestrator.LastUserSyncOptions.Build());
            }
            catch (Exception ex)
            {
                SyncOrchestrator.IsResyncAfterIdFix = false;
                Log.Error($"Pre-sync ID fix / re-sync failed: {ex.Message}");
                SyncStatusToast.Hide();
                TaskDialog.Show("Perseus — Sync", "Perseus paused your sync to write element IDs but could not restart it:\n" +
                                ex.Message + "\n\nPlease press Sync with Central again.");
            }
        }

        public string GetName() => "Perseus Pre-Sync ID Fix";

        /// <summary>
        /// Clashes this user's sync would push but Perseus couldn't renumber. Explains why and lets the user decide:
        /// look at the elements (select + zoom, sync stays cancelled), sync anyway (the clash reaches central and is
        /// fixed later), or cancel. True = go ahead with the sync. Main thread, outside any sync event.
        /// </summary>
        internal static bool AskAboutClashes(UIApplication app, IdClaims.FixResult clashes)
        {
            SyncStatusToast.Hide();
            var items = clashes.Unfixable;
            string list = string.Join("\n", items.Take(8).Select(c => $"• {c.Value}  ({c.Parameter})")) + (items.Count > 8 ? "\n• ..." : "");
            string why = clashes.Reasons.Count > 0
                ? "Perseus couldn't renumber " + (items.Count == 1 ? "it" : "them") + " because " + string.Join("; ", clashes.Reasons) + "."
                : "Perseus couldn't renumber " + (items.Count == 1 ? "it" : "them") + ".";
            string other = IdClaims.Describe(clashes);

            var dlg = new TaskDialog("Perseus — ID clash")
            {
                MainInstruction = items.Count == 1
                    ? "An ID in this model is already used by another element"
                    : $"{items.Count} IDs in this model are already used by other elements",
                MainContent = list + "\n\n" + why + (string.IsNullOrEmpty(other) ? "" : "\n\n" + other),
                FooterText = "You can change the value by hand, or run Audit IDs, then sync again.",
            };
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show me " + (items.Count == 1 ? "the element" : "the elements"),
                               "Selects and zooms to them. Your sync stays cancelled.");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sync anyway",
                               "The duplicate goes to central and is renumbered by a later sync.");
            dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Cancel sync");

            var answer = dlg.Show();
            if (answer == TaskDialogResult.CommandLink2)
            {
                Log.Info($"[IdClaims] user chose to sync with {items.Count} unresolved clash(es)");
                return true;
            }
            if (answer == TaskDialogResult.CommandLink1) ShowElements(app, items);
            Log.Info("[IdClaims] sync left cancelled over unresolved clash(es)");
            return false;
        }

        private static void ShowElements(UIApplication app, List<IdClaimItem> items)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                var ids = items.Select(c => uidoc.Document.GetElement(c.Uid)).Where(x => x != null).Select(x => x.Id).Distinct().ToList();
                if (ids.Count == 0) return;
                uidoc.Selection.SetElementIds(ids);
                try { uidoc.ShowElements(ids); } catch { /* selection is still set */ }
            }
            catch (Exception ex) { Log.Warn($"[IdClaims] could not show clashing elements: {ex.Message}"); }
        }
    }
}
