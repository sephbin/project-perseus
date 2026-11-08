using Autodesk.Revit.DB;
using ProjectPerseus.logging;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// The symbolic link from a review record to a linked element: link instance UniqueId + linked element UniqueId
    /// (authoritative), plus a stable Reference string that Selection.SetReferences can use to select the linked
    /// element directly.
    /// </summary>
    internal static class LinkedElementRef
    {
        internal static RevitLinkInstance FindInstance(Document host, string linkInstanceUid) =>
            string.IsNullOrEmpty(linkInstanceUid) ? null : host.GetElement(linkInstanceUid) as RevitLinkInstance;

        /// <summary>The linked element in the link's current version, or null (deleted / link unloaded).</summary>
        internal static Element Resolve(RevitLinkInstance instance, string linkedElementUid)
        {
            Document linkDoc = instance?.GetLinkDocument();
            return linkDoc == null || string.IsNullOrEmpty(linkedElementUid) ? null : linkDoc.GetElement(linkedElementUid);
        }

        /// <summary>Stable representation of the host-side reference to the linked element, or null.</summary>
        internal static string StableRepresentation(Document host, RevitLinkInstance instance, Element linked)
        {
            if (instance == null || linked == null) return null;
            try { return new Reference(linked).CreateLinkReference(instance).ConvertToStableRepresentation(host); }
            catch (System.Exception ex)
            {
                Log.Info($"[Federator] no stable reference for linked element {linked.UniqueId}: {ex.Message}");
                return null;
            }
        }

        internal static Reference ParseReference(Document host, string stable)
        {
            if (string.IsNullOrEmpty(stable)) return null;
            try { return Reference.ParseFromStableRepresentation(host, stable); }
            catch { return null; }
        }
    }
}
