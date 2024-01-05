using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.revit.adapters;
using Element = ProjectPerseus.models.Element;

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
                CreatedElements = RetrieveElements(docDiff.GetCreatedElementIds()),
                ModifiedElements = RetrieveElements(docDiff.GetModifiedElementIds()),
                DeletedElements = RetrieveElements(docDiff.GetDeletedElementIds())
            };
        }

        private IList<Element> RetrieveElements(ISet<ElementId> elementIds)
        {
            return elementIds.Select(id => new Element(new ArdbElementAdapter(_doc.GetElement(id)))).ToList();
        }
    }
}