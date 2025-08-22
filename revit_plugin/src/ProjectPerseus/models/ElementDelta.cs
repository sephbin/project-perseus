using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus.revit;

namespace ProjectPerseus.models
{
    public class ElementDelta
    {
        public ElementDelta(DeltaAction action, IArdbElement element)
        {
            Action = action;
            Element = new Element(element);
        }

        [JsonProperty("action")]
        [JsonConverter(typeof(StringEnumConverter))]
        public DeltaAction Action { get; }
        
        [JsonProperty("element")]
        public Element Element { get; }
        
        public static List<ElementDelta> CreateListFromChangeSet(ElementChangeSet changeSet)
        {
            var deltas = new List<ElementDelta>();
            deltas.AddRange(CreateList(DeltaAction.Create, changeSet.CreatedElements));
            deltas.AddRange(CreateList(DeltaAction.Update, changeSet.ModifiedElements));
            // deltas.AddRange(CreateList(DeltaAction.Delete, changeSet.DeletedElements)); TODO: Implement
            return deltas;
        }

        public static IList<ElementDelta> CreateList(DeltaAction action, IEnumerable<IArdbElement> elements)
        {
            return elements.Select(element => new ElementDelta(action, element)).ToList();
        }
        
        public enum DeltaAction
        {
            Create,
            Update,
            Delete
        }
    }
}