using System.Collections.Generic;
using System.Linq;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus
{
    public class ElementDataExtractor
    {
        private readonly ARDB.Document _doc;

        public ElementDataExtractor(ARDB.Document doc)
        {
            _doc = doc;
        }

        public List<ARDB.Element> Extract(ARDB.Category category)
        {
            var coll
                = new ARDB.FilteredElementCollector(_doc);

            return coll.WherePasses(new ARDB.ElementCategoryFilter(category.Id)).ToList();
            // elementCollector.Select(x => )
            //
            // var elements = new List<ARDB.Element>();
            // var categories = new List<ARDB.Category>();
            // using (var iterator = elementCollector.GetElementIterator())
            // {
            //     while (iterator.MoveNext())
            //     {
            //         using (var element = iterator.GetCurrent())
            //         {
            //             // elements.Add(element);
            //             if (element.Category != null &&
            //                 !categories.Exists(e => e.Id.IntegerValue == element.Category.Id.IntegerValue))
            //                 categories.Add(element.Category);
            //         }
            //     }
            // }
            //
            // ElementsToJsonFile(elements);
        }

        // private static void ElementsToJsonFile(IReadOnlyCollection<ARDB.Element> elements)
        // {
        //     Utl.PrettyWriteJson(elements, "./elements.json", null);
        // }

        // private class Element
        // {
        //     public int Id { get; } // todo: this can change apparently
        //
        //     public string Name { get; }
        //
        //     private Element(int id,
        //         string name)
        //     {
        //         Id = id;
        //         Name = name;
        //     }
        //
        //     public Element(ARDB.Element element) : this(
        //         element.Id.IntegerValue,
        //         element.Name)
        //     {
        //     }
        // }
    }
}