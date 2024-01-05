using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.models.interfaces;
using ProjectPerseus.revit.adapters;

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

        private List<IArdbElement> RetrieveElements(ISet<ElementId> elementIds)
        {
            return elementIds.Select(id => (IArdbElement)new ArdbElementAdapter(_doc.GetElement(id))).ToList();
        }
    }
}