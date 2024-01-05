using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.models.interfaces;

namespace ProjectPerseus.revit
{
    public class RevitFacade
    {
        public readonly Document Document;

        public RevitFacade(Document document)
        {
            Document = document;
        }

        public IList<IArdbElement> GetAllElements()
        {
            Log.Info("Extracting all elements from Revit...");
            return new ElementExtractor(Document).ExtractElements();
        }

        public ElementChangeSet GetElementChangeSet(Guid sinceVersionGuid)
        {
            Log.Info("Extracting changed elements from Revit...");
            return new ElementChangeSetGenerator(Document).ExtractChangedElements(sinceVersionGuid);
        }

        public static Guid GetDocumentVersionGuid(Document doc)
        {
            return Document.GetDocumentVersion(doc).VersionGUID;
        }
    }
}