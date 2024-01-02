using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.revit.adapters;
using ARDB = Autodesk.Revit.DB;
using Element = ProjectPerseus.models.Element;

namespace ProjectPerseus.revit
{
    public class ElementExtractor
    {
        private readonly Document _doc;
        public ElementExtractor(Document doc)
        {
            _doc = doc;
        }

        public List<Element> ExtractElements()
        {
            var allElements = getElementsFromRevit();

            var extractedElements = new List<Element>();

            foreach (var element in allElements)
            {
                var adapter = new ArdbElementAdapter(element);
                extractedElements.Add(new Element(adapter));
            }

            return extractedElements;
        }

        private IList<ARDB.Element> getElementsFromRevit()
        {
            // Create a filtered element collector to get all elements in the document
            var collector = new FilteredElementCollector(_doc);

            // Use the WhereElementIsNotElementType filter to exclude element types
            var allElements = collector.WhereElementIsNotElementType().ToElements();
            return allElements;
        }
    }
}