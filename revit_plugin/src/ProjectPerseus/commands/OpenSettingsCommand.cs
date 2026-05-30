using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            new SettingsForm().ShowDialog();
            return Result.Succeeded;
        }
    }
}
