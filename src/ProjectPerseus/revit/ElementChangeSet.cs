using System;
using System.Collections.Generic;
using Element = ProjectPerseus.models.Element;

namespace ProjectPerseus.revit
{
    public class ElementChangeSet
    {
        public Guid FromVersionGuid { get; set; }
        public Guid ToVersionGuid { get; set; }
        public IList<Element> CreatedElements { get; set; }
        public IList<Element> ModifiedElements { get; set; }
        public IList<Element> DeletedElements { get; set; }
    }
}