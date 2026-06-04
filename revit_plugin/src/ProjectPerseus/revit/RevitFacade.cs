using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.models;
using ProjectPerseus.revit.interfaces;
using ProjectPerseus;

using ProjectPerseus.logging;
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
            this IEnumerable<ElementDelta> deltas, IEnumerable<string> categoryNames)
        {
            if (categoryNames == null || !categoryNames.Any())
                throw new ArgumentException("Category names list must not be null or empty.", nameof(categoryNames));

            var categorySet = new HashSet<string>(
                categoryNames.Select(n => n.ToLowerInvariant())
            );

            var kept = new List<ElementDelta>();
            int nullCategoryCount = 0;
            foreach (var d in deltas)
            {
                var catName = d.Element?.originalElement?.CategoryName?.Name;
                if (catName != null && categorySet.Contains(catName.ToLowerInvariant()))
                {
                    kept.Add(d);
                }
                else if (catName == null)
                {
                    // Null-category elements are logged individually — they may indicate
                    // corrupted elements that should have been included (e.g. a Room whose
                    // category accessor returned null).
                    nullCategoryCount++;
                    var id = d.Element?.originalElement?.Id?.Value.ToString() ?? "?";
                    var name = d.Element?.Name ?? "?";
                    Log.Info($"FilterByCategoryName: element_id={id} name='{name}' has null category — dropped");
                }
                // Non-null categories that don't match the filter are expected (most elements);
                // we don't log them to avoid flooding the log with thousands of lines.
            }
            if (nullCategoryCount > 0)
                Log.Info($"FilterByCategoryName: {nullCategoryCount} elements had null category and were dropped");
            Log.Info($"FilterByCategoryName: {kept.Count} elements kept from category filter");
            return kept;
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