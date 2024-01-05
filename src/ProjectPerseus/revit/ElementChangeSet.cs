using System;
using System.Collections.Generic;

namespace ProjectPerseus.revit
{
    public class ElementChangeSet
    {
        public Guid FromVersionGuid { get; set; }
        public Guid ToVersionGuid { get; set; }
        public IList<int> CreatedElementIds { get; set; }
        public IList<int> ModifiedElementIds { get; set; }
        public IList<int> DeletedElementIds { get; set; }
    }
}