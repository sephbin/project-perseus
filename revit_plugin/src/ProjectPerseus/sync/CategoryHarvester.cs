using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace ProjectPerseus.sync
{
    // Enumerates every category visible in a Revit document — including categories
    // that have no BuiltInCategory enum entry (type-only categories, Cover Type, Mass
    // sub-types, Location Data, etc). Three passes deduplicate via ElementId.
    internal static class CategoryHarvester
    {
        public static IEnumerable<Category> GetAllCategories(Document doc)
        {
            var seen = new HashSet<ElementId>();

            // Pass 1: standard category tree + all sub-categories
            foreach (var cat in WalkCategoryMap(doc.Settings.Categories, seen))
                yield return cat;

            // Pass 2: named BuiltInCategory enum members (annotation, analytical, etc.)
            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                Category cat = null;
                try { cat = Category.GetCategory(doc, bic); } catch { }
                if (cat != null && seen.Add(cat.Id))
                    yield return cat;
            }

            // Pass 3: scan instances then types — catches internal categories with no
            // BuiltInCategory enum entry, including type-only categories.
            foreach (Element elem in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var cat = elem.Category;
                if (cat != null && seen.Add(cat.Id))
                    yield return cat;
            }
            foreach (Element elem in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                var cat = elem.Category;
                if (cat != null && seen.Add(cat.Id))
                    yield return cat;
            }
        }

        private static IEnumerable<Category> WalkCategoryMap(CategoryNameMap map, HashSet<ElementId> seen)
        {
            foreach (Category cat in map)
            {
                if (seen.Add(cat.Id))
                    yield return cat;
                if (cat.SubCategories != null && cat.SubCategories.Size > 0)
                    foreach (var sub in WalkCategoryMap(cat.SubCategories, seen))
                        yield return sub;
            }
        }
    }
}
