using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;
using Autodesk.Revit.DB;
using ProjectPerseus;

namespace ProjectPerseus.models
{
    public class ElementDelta
    {
        private readonly Document _doc;

        public ElementDelta(DeltaAction action, IArdbElement element, Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            Action = action;
            Element = new Element(element, _doc);
        }

        [JsonProperty("action")]
        [JsonConverter(typeof(StringEnumConverter))]
        public DeltaAction Action { get; }
        
        [JsonProperty("element")]
        public Element Element { get; }
        
        public static List<ElementDelta> CreateListFromChangeSet(ElementChangeSet changeSet, Document doc)
        {
            var deltas = new List<ElementDelta>();
            deltas.AddRange(CreateList(DeltaAction.Create, changeSet.CreatedElements, doc));
            deltas.AddRange(CreateList(DeltaAction.Update, changeSet.ModifiedElements, doc));
            // deltas.AddRange(CreateList(DeltaAction.Delete, changeSet.DeletedElements)); TODO: Implement
            return deltas;
        }

        public static IList<ElementDelta> CreateList(DeltaAction action, IEnumerable<IArdbElement> elements, Document doc)
        {
            //return elements.Select(element => new ElementDelta(action, element, doc)).ToList();
            var deltas = new List<ElementDelta>();
            int count = 0;
            int total = elements.Count();
            int logInterval = Math.Max(1, total / 100); // log ~100 times total

            
            foreach (var element in elements)
            {
                deltas.Add(new ElementDelta(action, element, doc));
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

            return deltas;
        }
        
        public enum DeltaAction
        {
            Create,
            Update,
            Delete
        }
    }
}