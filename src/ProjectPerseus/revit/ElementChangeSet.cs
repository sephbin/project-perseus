using System;
using System.Collections.Generic;
using ProjectPerseus.models.interfaces;

namespace ProjectPerseus.revit
{
    public class ElementChangeSet
    {
        public Guid FromVersionGuid { get; set; }
        public Guid ToVersionGuid { get; set; }
        public IList<IArdbElement> CreatedElements { get; set; }
        public IList<IArdbElement> ModifiedElements { get; set; }
        public IList<IArdbElement> DeletedElements { get; set; }
    }
}