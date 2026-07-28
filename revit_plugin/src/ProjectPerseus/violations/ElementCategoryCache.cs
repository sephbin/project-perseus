using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.violations
{
    internal class ElementInfo
    {
        public string CategoryName { get; }
        public string UniqueId     { get; }
        public string Name         { get; }
        public ElementInfo(string categoryName, string uniqueId, string name)
        { CategoryName = categoryName; UniqueId = uniqueId; Name = name ?? ""; }
    }

    internal static class ElementCategoryCache
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>> _cache
            = new ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>>();

        // Called from OnDocumentOpened on the main thread (FilteredElementCollector requires it).
        internal static void Prime(Document doc, string docGuid)
        {
            if (string.IsNullOrEmpty(docGuid)) return;
            var map = _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>());
            int count = 0;
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category == null) continue;
                map[el.Id.GetIdValue()] = new ElementInfo(el.Category.Name, el.UniqueId, el.Name ?? "");
                count++;
            }
            Log.Info($"[ElementCategoryCache] Primed {count} elements for {docGuid}");
        }

        // Called from OnDocumentChanged for added and modified elements.
        internal static void Track(string docGuid, long elementId, string categoryName, string uniqueId, string name)
        {
            if (string.IsNullOrEmpty(docGuid)) return;
            _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>())
                  [elementId] = new ElementInfo(categoryName, uniqueId, name);
        }

        internal static ElementInfo Get(string docGuid, long elementId) =>
            _cache.TryGetValue(docGuid, out var map) && map.TryGetValue(elementId, out var info)
                ? info : null;
    }
}
