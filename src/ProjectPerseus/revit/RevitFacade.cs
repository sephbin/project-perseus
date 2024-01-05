using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.models.interfaces;

namespace ProjectPerseus.revit
{
    public class RevitFacade
    {
        private Document _doc;

        public RevitFacade(Document doc)
        {
            _doc = doc;
        }

        public IList<IArdbElement> GetAllElements()
        {
            Log.Info("Extracting all elements from Revit...");
            return new ElementExtractor(_doc).ExtractElements();
        }

        public ElementChangeSet GetElementChangeSet(Guid sinceVersionGuid)
        {
            Log.Info("Extracting changed elements from Revit...");
            return new ElementChangeSetGenerator(_doc).ExtractChangedElements(sinceVersionGuid);
        }
    }
}