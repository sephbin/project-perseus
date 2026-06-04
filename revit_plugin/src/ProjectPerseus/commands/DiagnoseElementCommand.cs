using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.revit.adapters;
using ProjectPerseus.ui;
using ProjectPerseus.util;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DiagnoseElementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            using (var form = new DiagnoseElementForm())
            {
                if (ElementWatchList.Ids.Count > 0)
                    form.SetElementIds(ElementWatchList.Ids);

                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;

                var ids = form.GetElementIds();
                if (ids.Count == 0)
                {
                    TaskDialog.Show("Perseus — Diagnose", "No valid element IDs entered.");
                    return Result.Cancelled;
                }

                ElementWatchList.Set(ids);
                Log.Info($"DiagnoseElementCommand: watch list set to [{string.Join(", ", ids)}]");

                var docGuid = revit.ModelGuidStorage.GetOrCreate(doc);

                foreach (var rawId in ids)
                    DiagnoseElement(doc, rawId, docGuid);

                TaskDialog.Show("Perseus — Diagnose",
                    $"Diagnostic complete for {ids.Count} element(s).\n\n" +
                    "These IDs are now in the watch list. Run Full Sync to see exactly where each element is dropped (or not).\n\n" +
                    "Check the Perseus log file for results.");
            }

            return Result.Succeeded;
        }

        private static void DiagnoseElement(Document doc, long rawId, string docGuid)
        {
            Log.Info($"=== DiagnoseElement: BEGIN id={rawId} ===");
            try
            {
                var eid = RevitExtensions.CreateId(rawId);

                // 1. Raw doc.GetElement
                var revitElem = doc.GetElement(eid);
                if (revitElem == null)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: doc.GetElement returned null — element does not exist in document");
                    Log.Info($"=== DiagnoseElement: END id={rawId} ===");
                    return;
                }

                Log.Info($"DiagnoseElement [{rawId}]: Name='{revitElem.Name}' UniqueId='{revitElem.UniqueId}'");
                Log.Info($"DiagnoseElement [{rawId}]: Category='{revitElem.Category?.Name ?? "<null>"}' IsElementType={revitElem is ElementType}");

                // 2. Workset info
                try
                {
                    if (doc.IsWorkshared)
                    {
                        var worksetId = revitElem.WorksetId;
                        Log.Info($"DiagnoseElement [{rawId}]: WorksetId={worksetId?.IntegerValue} (InvalidWorkset={WorksetId.InvalidWorksetId?.IntegerValue})");
                        if (worksetId != WorksetId.InvalidWorksetId)
                        {
                            var workset = doc.GetWorksetTable().GetWorkset(worksetId);
                            Log.Info($"DiagnoseElement [{rawId}]: Workset='{workset?.Name ?? "<null>"}' IsOpen={workset?.IsOpen}");
                        }
                    }
                    else
                    {
                        Log.Info($"DiagnoseElement [{rawId}]: document is not workshared");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: workset check threw {ex.GetType().Name}: {ex.Message}");
                }

                // 3. FilteredElementCollector scoped check — confirms whether GetAllElements() would collect this element
                try
                {
                    var scopedCollector = new FilteredElementCollector(doc, new List<ElementId> { eid })
                        .WhereElementIsNotElementType();
                    var found = scopedCollector.FirstElement();
                    Log.Info($"DiagnoseElement [{rawId}]: FilteredElementCollector(scoped).WhereElementIsNotElementType() = {(found != null ? "FOUND" : "NOT FOUND (is ElementType or collector excluded it)")}");
                }
                catch (Exception ex)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: scoped collector threw {ex.GetType().Name}: {ex.Message}");
                }

                // 4. Raw Revit Parameters
                try
                {
                    var paramSet = revitElem.Parameters;
                    int paramCount = 0;
                    var paramLog = new StringBuilder();
                    foreach (Parameter p in paramSet)
                    {
                        paramCount++;
                        string val;
                        try
                        {
                            switch (p.StorageType)
                            {
                                case Autodesk.Revit.DB.StorageType.String:    val = $"\"{p.AsString()}\""; break;
                                case Autodesk.Revit.DB.StorageType.Integer:   val = p.AsInteger().ToString(); break;
                                case Autodesk.Revit.DB.StorageType.Double:    val = p.AsDouble().ToString("G6"); break;
                                case Autodesk.Revit.DB.StorageType.ElementId: val = p.AsElementId()?.GetIdValue().ToString(); break;
                                default: val = p.StorageType.ToString(); break;
                            }
                        }
                        catch { val = "<error reading value>"; }
                        paramLog.AppendLine($"  param[{paramCount}]: '{p.Definition?.Name}' StorageType={p.StorageType} Value={val}");
                    }
                    Log.Info($"DiagnoseElement [{rawId}]: raw Revit Parameters count={paramCount}");
                    if (paramCount > 0)
                        Log.Info($"DiagnoseElement [{rawId}]: raw param list:\n{paramLog}");
                }
                catch (Exception ex)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: raw Parameters enumeration threw {ex.GetType().Name}: {ex.Message}");
                }

                // 5. ArdbElementAdapter path
                try
                {
                    var adapter = new ArdbElementAdapter(revitElem);
                    Log.Info($"DiagnoseElement [{rawId}]: adapter.Name='{adapter.Name}' adapter.CategoryName='{adapter.CategoryName?.Name ?? "<null>"}'");

                    int adapterParamCount = 0;
                    foreach (var _ in adapter.ParametersSet)
                        adapterParamCount++;
                    Log.Info($"DiagnoseElement [{rawId}]: adapter.ParametersSet count={adapterParamCount}");
                }
                catch (Exception ex)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: ArdbElementAdapter threw {ex.GetType().Name}: {ex.Message}");
                }

                // 6. Full plugin serialisation path via ElementDelta — triggers all GetParameters() logging
                try
                {
                    var adapter = new ArdbElementAdapter(revitElem);
                    var delta = new ElementDelta(ElementDelta.DeltaAction.Create, adapter, doc, docGuid);
                    var pluginParams = delta.Element.Parameters;
                    Log.Info($"DiagnoseElement [{rawId}]: ElementDelta.Element.Parameters count={pluginParams?.Count ?? -1} (includes synthetics)");
                }
                catch (Exception ex)
                {
                    Log.Info($"DiagnoseElement [{rawId}]: ElementDelta path threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"DiagnoseElement [{rawId}]: unhandled exception {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            Log.Info($"=== DiagnoseElement: END id={rawId} ===");
        }
    }
}
