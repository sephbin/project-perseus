using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ProjectPerseus.models.interfaces;
using ProjectPerseus.revit;

namespace ProjectPerseus.models
{
    public class ElementDelta : Element
    {
        public ElementDelta(DeltaAction action, IArdbElement element) : base(element)
        {
            Action = action;
        }

        [JsonProperty("action")]
        public DeltaAction Action { get; }
        
        public static List<ElementDelta> CreateListFromChangeSet(ElementChangeSet changeSet)
        {
            var deltas = new List<ElementDelta>();
            deltas.AddRange(CreateList(DeltaAction.Create, changeSet.CreatedElements));
            deltas.AddRange(CreateList(DeltaAction.Modify, changeSet.ModifiedElements));
            deltas.AddRange(CreateList(DeltaAction.Delete, changeSet.DeletedElements));
            return deltas;
        }

        public static IList<ElementDelta> CreateList(DeltaAction action, IEnumerable<IArdbElement> elements)
        {
            return elements.Select(element => new ElementDelta(action, element)).ToList();
        }
        
        public enum DeltaAction
        {
            Create,
            Modify,
            Delete
        }
    }
}