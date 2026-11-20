using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.ui;

namespace ProjectPerseus.commands
{
    /// <summary>
    /// Ribbon: Button_EditAutoParameters ("Rules", Auto Parameters panel). Opens ui/AutoParametersWindow to edit the
    /// model's / project's Auto Parameters rules on the server from Revit. After a save it offers to run Audit so
    /// existing elements follow the new rules (the window only talks to the server; the Audit needs this API context,
    /// hence TransactionMode.Manual).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class EditAutoParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;
                var doc = uiapp.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    TaskDialog.Show("Perseus — Auto Parameters", "Open a project model to edit its Auto Parameters.");
                    return Result.Cancelled;
                }
                string baseUrl = Config.Instance.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    TaskDialog.Show("Perseus — Auto Parameters", "Please configure the server URL in Settings first.");
                    return Result.Failed;
                }

                string docGuid = ModelGuidStorage.GetOrCreate(doc);
                if (string.IsNullOrEmpty(docGuid))
                {
                    TaskDialog.Show("Perseus — Auto Parameters",
                        "This model has no Perseus identity yet (it couldn't be written to). Open it editable, or sync it once, then try again.");
                    return Result.Cancelled;
                }

                var dlg = new AutoParametersWindow(baseUrl, docGuid, doc.Title, ModelCategories(doc));
                new System.Windows.Interop.WindowInteropHelper(dlg).Owner = uiapp.MainWindowHandle;
                dlg.ShowDialog();
                Log.Info($"[EditAutoParametersCommand] editor closed for {docGuid}; saved: {dlg.Saved}");

                if (dlg.Saved && !doc.IsReadOnly)
                {
                    var td = new TaskDialog("Perseus — Auto Parameters")
                    {
                        MainInstruction = "Rules saved",
                        MainContent = "New and edited elements follow the new rules from now on. Run Audit now to bring the " +
                                      "existing elements up to date (it may check out elements you don't own yet)?",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No,
                    };
                    if (td.Show() == TaskDialogResult.Yes)
                        return new AuditIdsCommand().Execute(commandData, ref message, elements);
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        // Model categories that can carry instance parameters: what an Auto Parameters rule can target here.
        private static IEnumerable<string> ModelCategories(Document doc)
        {
            var names = new List<string>();
            try
            {
                foreach (Category c in doc.Settings.Categories)
                    if (c != null && c.CategoryType == CategoryType.Model && c.AllowsBoundParameters && !string.IsNullOrEmpty(c.Name))
                        names.Add(c.Name);
            }
            catch (Exception ex) { Log.Warn($"[EditAutoParametersCommand] categories: {ex.Message}"); }
            return names.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
