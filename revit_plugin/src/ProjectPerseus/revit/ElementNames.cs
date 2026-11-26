using Autodesk.Revit.DB;

namespace ProjectPerseus.revit
{
    /// <summary>
    /// Element names without calling Element.Name on family instances. FamilyInstance.Name is just its type's name,
    /// so it is read from the type element instead. 2026-11-25: after a script flipped doors with
    /// FamilyInstance.FlipFromToRoom, Perseus reading the door's Name (ViolationDetector on DocumentChanged,
    /// ElementCategoryCache priming) was followed by native heap corruption (double free in EssentialsDB) and Revit
    /// crashing within minutes - five times. Later (2026-11-26) Revit crashed the same way with Perseus off: the
    /// script itself is the cause. Kept because it is harmless and equivalent. Use this for any element
    /// that may be an instance; Element.Name stays fine for types, views, levels, rooms etc.
    /// </summary>
    internal static class ElementNames
    {
        internal static string Of(Element el)
        {
            if (el == null) return "";
            if (el is FamilyInstance)
            {
                try
                {
                    var typeId = el.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                        return el.Document.GetElement(typeId)?.Name ?? "";
                }
                catch { /* fall through to "" */ }
                return "";
            }
            return el.Name ?? "";
        }
    }
}
