using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;
using Autodesk.Revit.DB;
using ProjectPerseus;
using ProjectPerseus.revit.adapters;

namespace ProjectPerseus.models
{
    public class ElementDelta
    {
        private readonly Document _doc;
        private readonly string _docGuid;

        public ElementDelta(DeltaAction action, IArdbElement element, Document doc, string docGuid)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _docGuid = docGuid ?? throw new ArgumentNullException(nameof(docGuid));
            Action = action;
            Element = new Element(element, _doc, _docGuid);
        }

        [JsonProperty("action")]
        [JsonConverter(typeof(StringEnumConverter))]
        public DeltaAction Action { get; }
        
        [JsonProperty("element")]
        public Element Element { get; }
        
        public static List<ElementDelta> CreateListFromChangeSet(ElementChangeSet changeSet, Document doc, string docGuid)
        {
            var deltas = new List<ElementDelta>();
            deltas.AddRange(CreateList(DeltaAction.Create, changeSet.CreatedElements, doc, docGuid));
            deltas.AddRange(CreateList(DeltaAction.Update, changeSet.ModifiedElements, doc, docGuid));
            //deltas.AddRange(CreateList(DeltaAction.Delete, changeSet.DeletedElements, doc, docGuid)); //TODO: Implement
            Utl.WriteLog($"CreateListFromChangeSet, before return");
            return deltas;
        }

        public static List<int> CreateDeletedListFromChangeSet(ElementChangeSet changeSet)
        {
            try
            {
                if (changeSet?.DeletedElementIds == null)
                {
                    Utl.WriteLog("CreateDeletedListFromChangeSet: no deleted IDs found.");
                    return new List<int>();
                }

                var deletedIds = changeSet.DeletedElementIds.ToList(); // clone in case it's reused
                Utl.WriteLog($"CreateDeletedListFromChangeSet: Found {deletedIds.Count} deleted element IDs.");
                return deletedIds;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"CreateDeletedListFromChangeSet failed: {ex.Message}");
                return new List<int>();
            }
        }

        public static IList<ElementDelta> CreateList(DeltaAction action, IEnumerable<IArdbElement> elements, Document doc,  string docGuid)
        {
            //return elements.Select(element => new ElementDelta(action, element, doc)).ToList();
            Utl.WriteLog($"CreateList {action}");
            var deltas = new List<ElementDelta>();
            int count = 0;
            int total = elements.Count();
            int logInterval = Math.Max(1, total / 10); // log ~100 times total

            
            foreach (var element in elements)
            {
                //Utl.WriteLog("Add ElementDelta to list");
                deltas.Add(new ElementDelta(action, element, doc, docGuid));
                count++;

                if (count % logInterval == 0 || count == total)
                {
                    // Write progress to the plugin log file
                    try
                    {
                        Utl.WriteLog($"CreateList: Processed {count} of {total} ({(count * 100 / total)}%) elements");
                    }
                    catch
                    {
                        // Don't crash if logging fails
                    }
                }
            }
            Utl.WriteLog($"CreateList {action}, before return");
            return deltas;
        }
        
        public enum DeltaAction
        {
            Create,
            Update,
            Delete
        }
        

        /// <summary>
        /// Scans a list of ElementDeltas and extracts any Parameter value that is an ElementId.
        /// </summary>
        public static HashSet<int> GetReferencedIds(List<ElementDelta> sourceDeltas)
        {
            var foundIds = new HashSet<int>();

            foreach (var delta in sourceDeltas)
            {
                // Skip deletions, they have no parameters
                if (delta.Action == DeltaAction.Delete) continue;

                foreach (var param in delta.Element.Parameters)
                {
                    // Check if this parameter stores an ElementId
                    // Note: ValueType is a string like "ElementId" based on your ParameterBase class
                    if (param.ValueType == "ElementId" && param.Value is int idValue)
                    {
                        // Filter out -1 (Invalid) and 0, and maybe small internal numbers
                        if (idValue > 0)
                        {
                            foundIds.Add(idValue);
                        }
                    }
                }
            }
            return foundIds;
        }

        /// <summary>
        /// Manually creates a list of ElementDeltas from a list of Integer IDs.
        /// </summary>
        public static List<ElementDelta> CreateListFromIds(IEnumerable<int> ids, Document doc, string docGuid)
        {
            var deltas = new List<ElementDelta>();

            foreach (int id in ids)
            {
                try
                {
                    ElementId eid = new ElementId(id);
                    Autodesk.Revit.DB.Element revitElem = doc.GetElement(eid);

                    if (revitElem != null)
                    {
                        // We need to wrap the raw Revit Element in your Adapter
                        // Assuming you have ArdbElementAdapter in ProjectPerseus.revit.adapters
                        var elementAdapter = new ArdbElementAdapter(revitElem);

                        // We treat connected elements as "Update" actions (or Create). 
                        // "Update" is safer as it usually implies "Upsert" in databases.
                        deltas.Add(new ElementDelta(DeltaAction.Update, elementAdapter, doc, docGuid));
                    }
                }
                catch (Exception ex)
                {
                    Utl.WriteLog($"Error processing referenced ID {id}: {ex.Message}");
                }
            }
            return deltas;
        }
    }
}