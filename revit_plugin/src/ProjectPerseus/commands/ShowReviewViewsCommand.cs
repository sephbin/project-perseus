using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_ReviewViews (Federator panel). Edits the review views (the go-to views the federator panels use to show
    // elements) without opening a panel: ui/ReviewViewsWindow.EditFor.
    [Transaction(TransactionMode.Manual)]
    public class ShowReviewViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.ReviewViewsWindow.EditFor(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowReviewViewsCommand] {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
