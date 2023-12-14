
using System.Collections.Generic;
using Autodesk.Revit.DB;
namespace ProjectPerseus
{
    public class ElementExtractor
    {
        private Document doc;
        public ElementExtractor(Document doc)
        {
            this.doc = doc;
        }
        
        public List<models.Element> ExtractElements()
        {
            var allElements = getElementsFromRevit();

            var extractedElements = new List<models.Element>();

            foreach (var element in allElements)
            {
                extractedElements.Add(models.Element.FromARDBElement(element));
            }

            return extractedElements;
        }

        private IList<Element> getElementsFromRevit()
        {
            // Create a filtered element collector to get all elements in the document
            var collector = new FilteredElementCollector(doc);

            // Use the WhereElementIsNotElementType filter to exclude element types
            var allElements = collector.WhereElementIsNotElementType().ToElements();
            return allElements;
        }
    }
}