using System.Collections.Concurrent;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.violations
{
    internal class ElementInfo
    {
        public string CategoryName  { get; }
        public string UniqueId      { get; }
        public string Name          { get; }
        public string FamilyTypeName { get; }
        public ElementInfo(string categoryName, string uniqueId, string name, string familyTypeName = "")
        {
            CategoryName   = categoryName;
            UniqueId       = uniqueId;
            Name           = name ?? "";
            FamilyTypeName = familyTypeName ?? "";
        }
    }

    internal static class ElementCategoryCache
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>> _cache
            = new ConcurrentDictionary<string, ConcurrentDictionary<long, ElementInfo>>();

        internal static void Prime(Document doc, string docGuid)
        {
            if (string.IsNullOrEmpty(docGuid)) return;
            var map = _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>());
            int count = 0;
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.Category == null) continue;
                map[el.Id.GetIdValue()] = new ElementInfo(
                    el.Category.Name, el.UniqueId, el.Name ?? "", GetFamilyTypeName(el));
                count++;
            }
            Log.Info($"[ElementCategoryCache] Primed {count} elements for {docGuid}");
        }

        internal static void Track(string docGuid, long elementId,
            string categoryName, string uniqueId, string name, string familyTypeName = "")
        {
            if (string.IsNullOrEmpty(docGuid)) return;
            _cache.GetOrAdd(docGuid, _ => new ConcurrentDictionary<long, ElementInfo>())
                  [elementId] = new ElementInfo(categoryName, uniqueId, name, familyTypeName);
        }

        internal static ElementInfo Get(string docGuid, long elementId) =>
            _cache.TryGetValue(docGuid, out var map) && map.TryGetValue(elementId, out var info)
                ? info : null;

        internal static string GetFamilyTypeName(Element el)
        {
            try
            {
                var typeId = el.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeEl = el.Document.GetElement(typeId);
                    if (typeEl != null) return typeEl.Name ?? "";
                }
            }
            catch { }
            return "";
        }
    }
}
