using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ProjectPerseus.revit
{
    public class ElementChangeSetGenerator
    {
        private readonly Document _doc;
        public ElementChangeSetGenerator(Document doc)
        {
            _doc = doc;
        }
        
        public ElementChangeSet ExtractChangedElements(Guid sinceVersionGuid)
        {
            var docDiff = _doc.GetChangedElements(sinceVersionGuid);
            
            if(docDiff.AreDeletedElementIdsAvailable == false)
            {
                throw new Exception("Deleted element ids are not available.");
            }
            
            return new ElementChangeSet
            {
                FromVersionGuid = sinceVersionGuid,
                ToVersionGuid = Document.GetDocumentVersion(_doc).VersionGUID,
                CreatedElementIds = ElementIdsToIntegers(docDiff.GetCreatedElementIds()),
                ModifiedElementIds = ElementIdsToIntegers(docDiff.GetModifiedElementIds()),
                DeletedElementIds = ElementIdsToIntegers(docDiff.GetDeletedElementIds())
            };
        }

        private static List<int> ElementIdsToIntegers(IEnumerable<ElementId> elementIds)
        {
            return elementIds.Select(id => id.IntegerValue).ToList();
        }
    }
}