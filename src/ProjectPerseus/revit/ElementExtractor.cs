using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.models.interfaces;
using ProjectPerseus.revit.adapters;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.revit
{
    public class ElementExtractor
    {
        private readonly Document _doc;
        public ElementExtractor(Document doc)
        {
            _doc = doc;
        }

        public List<IArdbElement> ExtractElements()
        {
            var allElements = GetElementsFromRevit();

            var extractedElements = new List<IArdbElement>();

            foreach (var element in allElements)
            {
                extractedElements.Add(new ArdbElementAdapter(element));
            }

            return extractedElements;
        }

        private IList<ARDB.Element> GetElementsFromRevit()
        {
            // Create a filtered element collector to get all elements in the document
            var collector = new FilteredElementCollector(_doc);

            // Use the WhereElementIsNotElementType filter to exclude element types
            var allElements = collector.WhereElementIsNotElementType().ToElements();
            return allElements;
        }
    }
}