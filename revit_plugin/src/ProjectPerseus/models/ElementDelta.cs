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

using ProjectPerseus.logging;
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
            Log.Info($"CreateListFromChangeSet, before return");
            return deltas;
        }

        // 🔹 CHANGED: List<int> to List<long>
        public static List<long> CreateDeletedListFromChangeSet(ElementChangeSet changeSet)
        {
            try
            {
                if (changeSet?.DeletedElementIds == null)
                {
                    Log.Info("CreateDeletedListFromChangeSet: no deleted IDs found.");
                    return new List<long>(); // 🔹 CHANGED
                }

                var deletedIds = changeSet.DeletedElementIds.ToList(); // clone in case it's reused
                Log.Info($"CreateDeletedListFromChangeSet: Found {deletedIds.Count} deleted element IDs.");
                return deletedIds;
            }
            catch (Exception ex)
            {
                Log.Info($"CreateDeletedListFromChangeSet failed: {ex.Message}");
                return new List<long>(); // 🔹 CHANGED
            }
        }

        public static IList<ElementDelta> CreateList(DeltaAction action, IEnumerable<IArdbElement> elements, Document doc, string docGuid)
        {
            Log.Info($"CreateList {action}");
            var deltas = new List<ElementDelta>();
            int count = 0;
            int total = elements.Count();
            int logInterval = Math.Max(1, total / 10); // log ~100 times total

            foreach (var element in elements)
            {
                deltas.Add(new ElementDelta(action, element, doc, docGuid));
                count++;

                if (count % logInterval == 0 || count == total)
                {
                    try
                    {
                        Log.Info($"CreateList: Processed {count} of {total} ({(count * 100 / total)}%) elements");
                    }
                    catch
                    {
                        // Don't crash if logging fails
                    }
                }
            }
            Log.Info($"CreateList {action}, before return");
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
        // 🔹 CHANGED: HashSet<int> to HashSet<long>
        public static HashSet<long> GetReferencedIds(List<ElementDelta> sourceDeltas)
        {
            var foundIds = new HashSet<long>(); // 🔹 CHANGED

            foreach (var delta in sourceDeltas)
            {
                if (delta.Action == DeltaAction.Delete) continue;

                foreach (var param in delta.Element.Parameters)
                {
                    // 🔹 CHANGED: Check if Value is long instead of int
                    if (param.ValueType == "ElementId" && param.Value is long idValue)
                    {
                        if (idValue > 0)
                        {
                            foundIds.Add(idValue);
                        }
                    }
                    // Fallback just in case an older serialization structure fed it an int
                    else if (param.ValueType == "ElementId" && param.Value is int oldIdValue)
                    {
                        if (oldIdValue > 0)
                        {
                            foundIds.Add((long)oldIdValue);
                        }
                    }
                }
            }
            return foundIds;
        }

        /// <summary>
        /// Manually creates a list of ElementDeltas from a list of Integer/Long IDs.
        /// </summary>
        // 🔹 CHANGED: IEnumerable<int> to IEnumerable<long>
        public static List<ElementDelta> CreateListFromIds(IEnumerable<long> ids, Document doc, string docGuid)
        {
            var deltas = new List<ElementDelta>();

            foreach (long id in ids) // 🔹 CHANGED
            {
                try
                {
                    // 🔹 CHANGED: Use the cross-version extension method instead of new ElementId()
                    ElementId eid = RevitExtensions.CreateId(id);
                    Autodesk.Revit.DB.Element revitElem = doc.GetElement(eid);

                    if (revitElem != null)
                    {
                        var elementAdapter = new ArdbElementAdapter(revitElem);
                        deltas.Add(new ElementDelta(DeltaAction.Update, elementAdapter, doc, docGuid));
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"Error processing referenced ID {id}: {ex.Message}");
                }
            }
            return deltas;
        }
    }
}