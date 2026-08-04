using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.violations;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class FreefallCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (FreefallMode.IsActive)
            {
                var dlg = new TaskDialog("Perseus — Freefall Mode Active")
                {
                    MainIcon        = TaskDialogIcon.TaskDialogIconWarning,
                    MainInstruction = "Freefall mode is currently ON",
                    MainContent     = "Violation blocks and warnings are disabled. Freefall turns off automatically after your next sync.\n\nTurn it off now?",
                };
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Turn off Freefall mode");
                dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Keep Freefall on");
                if (dlg.Show() == TaskDialogResult.CommandLink1)
                {
                    FreefallMode.IsActive = false;
                    Log.Info("[FreefallMode] Disabled manually by user.");
                    ViolationDetector.RefreshOverlay(); // Re-evaluate overlay — violations may still be pending.
                }
                return Result.Succeeded;
            }

            var confirmDlg = new TaskDialog("Perseus — Enable Freefall Mode")
            {
                MainIcon        = TaskDialogIcon.TaskDialogIconError,
                MainInstruction = "Enable Freefall mode?",
                MainContent     = "Freefall mode disables all Perseus violation blocks and warnings for your next sync.\n\n"
                                + "Use this only if directed by your BIM manager, or if a block is preventing a necessary sync.\n\n"
                                + "Freefall mode will turn off automatically after your next sync.",
                FooterText      = "Enabling Freefall mode is logged.",
            };
            confirmDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Enable Freefall — I understand the risks");
            confirmDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel — keep protection on");

            if (confirmDlg.Show() != TaskDialogResult.CommandLink1)
                return Result.Succeeded;

            FreefallMode.IsActive = true;
            Log.Warn($"[FreefallMode] Enabled by {commandData.Application.Application.Username} ({System.Environment.UserName}).");

            var infoDialog = new TaskDialog("Perseus — Freefall Mode Enabled")
            {
                MainIcon        = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = "Freefall mode is now ON",
                MainContent     = "You can now sync without Perseus violation blocks. Freefall turns off automatically after your next sync.",
                CommonButtons   = TaskDialogCommonButtons.Ok,
            };
            infoDialog.Show();

            // Refresh after the user clicks OK — hides the overlay now that they have acknowledged
            // Freefall is on. Doing this post-dialog avoids a race between the async BeginInvoke
            // and the overlay's 150ms position timer that was still running during the dialog.
            ViolationDetector.RefreshOverlay();

            return Result.Succeeded;
        }
    }
}
