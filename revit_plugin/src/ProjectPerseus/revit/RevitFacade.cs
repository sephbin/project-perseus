using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.models;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus;

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
    public static class ElementDeltaFilters
    {
        /// <summary>
        /// Filters an ElementDelta list to only those whose Element.CategoryName matches the given value.
        /// </summary>
        public static List<ElementDelta> FilterByCategoryName(
            this IEnumerable<ElementDelta> deltas, string categoryName)
        {
            //foreach (var d in deltas)
            //{
            //    try { Utl.WriteLog(d.Element.originalElement.CategoryName.Name); } catch { }
            //}
            
            if (string.IsNullOrEmpty(categoryName))
                throw new ArgumentException("Category name must not be null or empty.", nameof(categoryName));

            return deltas
                .Where(d => d.Element?.originalElement.CategoryName != null &&
                            d.Element.originalElement.CategoryName.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Filters an ElementDelta list by multiple category names (OR condition).
        /// </summary>
        public static List<ElementDelta> FilterByCategories(
            this IEnumerable<ElementDelta> deltas, IEnumerable<string> categoryNames)
        {
            var categorySet = new HashSet<string>(
                categoryNames.Select(n => n.ToLowerInvariant()));

            return deltas
                .Where(d => d.Element?.originalElement.CategoryName != null &&
                            categorySet.Contains(d.Element.originalElement.CategoryName.Name.ToLowerInvariant()))
                .ToList();
        }
    }
}