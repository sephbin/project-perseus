using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.naming;
using ProjectPerseus.revit;
using ProjectPerseus.ui;

namespace ProjectPerseus.commands
{
    /// <summary>
    /// Ribbon: Button_ParameterNaming ("Parameter Naming", Setup panel). Opens ui/ParameterNamingWindow to edit the naming
    /// convention of the parameters Perseus creates (this model / project / server-wide). Renaming the parameters the
    /// model already has needs this API context, so it happens here after the window closes: when the user asked for it
    /// (Rename parameters…) or, after a save, when the model's names now differ from the convention (offered, never
    /// automatic). Each rename = SharedParameterManager.Rename (replace + copy values + record, one undo step).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class EditParameterNamingCommand : IExternalCommand
    {
        private const string Caption = "Perseus — Parameter Naming";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;
                var doc = uiapp.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    TaskDialog.Show(Caption, "Open a project model to edit its parameter naming.");
                    return Result.Cancelled;
                }
                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show(Caption, "Please configure the server URL in Settings first.");
                    return Result.Failed;
                }
                string docGuid = ModelGuidStorage.GetOrCreate(doc);
                if (string.IsNullOrEmpty(docGuid))
                {
                    TaskDialog.Show(Caption, "This model has no Perseus identity yet (it couldn't be written to). Open it editable, or sync it once, then try again.");
                    return Result.Cancelled;
                }

                var convention = ParameterNames.LoadNow(docGuid, baseUrl);
                bool hasProject = true; // the window drops the Project layer if the server says there is none
                var pending = ParameterNames.Pending(doc);
                var dlg = new ParameterNamingWindow(baseUrl, docGuid, doc.Title, hasProject, false, pending.Count);
                new System.Windows.Interop.WindowInteropHelper(dlg).Owner = uiapp.MainWindowHandle;
                dlg.ShowDialog();
                Log.Info($"[EditParameterNamingCommand] editor closed for {docGuid}; saved: {dlg.Saved}, rename: {dlg.RenameRequested}");

                if (!dlg.RenameRequested && !dlg.Saved) return Result.Succeeded;
                if (dlg.Saved) ParameterNames.LoadNow(docGuid, baseUrl);
                pending = ParameterNames.Pending(doc);
                if (pending.Count == 0)
                {
                    if (dlg.RenameRequested)
                        TaskDialog.Show(Caption, convention == null && ParameterNames.Convention(doc) == null
                            ? "The naming convention couldn't be loaded from the server, so there is nothing to rename to."
                            : "Every Perseus parameter in this model already has the convention's name.");
                    return Result.Succeeded;
                }
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show(Caption, "This model is read-only, so its parameters can't be renamed.");
                    return Result.Succeeded;
                }
                return OfferRename(doc, docGuid, pending, dlg.RenameRequested) ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool OfferRename(Document doc, string docGuid, System.Collections.Generic.List<(string Key, string Current, string Wanted)> pending, bool asked)
        {
            var list = new StringBuilder();
            foreach (var p in pending) list.AppendLine($"{p.Current}  →  {p.Wanted}");
            var td = new TaskDialog(Caption)
            {
                MainInstruction = asked ? $"Rename {pending.Count} Perseus parameter(s)?" : $"{pending.Count} parameter(s) in this model now differ from the convention",
                MainContent = list + "\nEach one is replaced by a parameter with the new name and its values are copied across (one undo step per parameter). " +
                              "Schedules, filters and tags that used the old parameter need pointing at the new one.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = asked ? TaskDialogResult.Yes : TaskDialogResult.No,
                FooterText = asked ? null : "You can do this later with Parameter Naming > Rename parameters.",
            };
            if (td.Show() != TaskDialogResult.Yes) return false;

            var report = new StringBuilder();
            int ok = 0;
            foreach (var p in pending)
            {
                string error = SharedParameterManager.Rename(doc, p.Key, out int copied, out int failed);
                if (error == null)
                {
                    ok++;
                    report.AppendLine($"✓ {p.Current} → {p.Wanted}: {copied} value(s) copied" + (failed > 0 ? $", {failed} failed" : ""));
                }
                else report.AppendLine($"✗ {p.Current}: {error}");
            }
            if (ok > 0) IdAssigner.InvalidateIndex(docGuid); // the ID index holds parameter GUIDs
            ParameterNames.Invalidate(doc);
            Log.Info($"[EditParameterNamingCommand] renamed {ok}/{pending.Count}:\n{report}");
            TaskDialog.Show(Caption, report.ToString().TrimEnd());
            return ok > 0;
        }
    }
}
