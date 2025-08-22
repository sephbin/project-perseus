using System;
using System.Collections.Generic;
using ProjectPerseus.revit.interfaces;

namespace ProjectPerseus.revit
{
    public class ElementChangeSet
    {
        public Guid FromVersionGuid { get; set; }
        public Guid ToVersionGuid { get; set; }
        public IList<IArdbElement> CreatedElements { get; set; }
        public IList<IArdbElement> ModifiedElements { get; set; }
        public IList<IArdbElement> DeletedElements { get; set; }

        public bool ContainsChanges()
        {
            return CreatedElements.Count > 0 || ModifiedElements.Count > 0 || DeletedElements.Count > 0;
        }
    }
}