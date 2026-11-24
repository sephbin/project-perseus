using System;
using System.IO;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// The model a federator comparison is about: a link instance (consultant model), or the open model itself
    /// ("This model": the current state compared against a copy of the file the user picks). Comparison records,
    /// review events and baked annotations store <see cref="Uid"/> where they store a link instance UniqueId; for the
    /// open model that is <see cref="HostUid"/>. Everything that resolves, selects or transforms compared elements
    /// goes through this, so the panel treats both the same. The open model is never archived or snapshot-cached
    /// live (it changes all the time); users pick their own saved copies to compare with.
    /// </summary>
    internal sealed class ComparedModel
    {
        /// <summary>Stored in place of a link instance UniqueId for comparisons of the open model itself.</summary>
        internal const string HostUid = "perseus:this-model";

        internal static bool IsHostUid(string uid) => string.Equals(uid, HostUid, StringComparison.Ordinal);

        internal Document Host { get; }
        /// <summary>The link instance; null for the open model.</summary>
        internal RevitLinkInstance Instance { get; }
        internal bool IsHost => Instance == null;
        internal string Uid => IsHost ? HostUid : Instance.UniqueId;

        private ComparedModel(Document host, RevitLinkInstance instance) { Host = host; Instance = instance; }

        internal static ComparedModel ForHost(Document host) => host == null ? null : new ComparedModel(host, null);

        internal static ComparedModel Of(Document host, RevitLinkInstance instance) =>
            host == null || instance == null ? null : new ComparedModel(host, instance);

        /// <summary>The compared model for a stored uid; null when that link instance is no longer in the model.</summary>
        internal static ComparedModel Find(Document host, string uid)
        {
            if (host == null || string.IsNullOrEmpty(uid)) return null;
            if (IsHostUid(uid)) return ForHost(host);
            return Of(host, LinkedElementRef.FindInstance(host, uid));
        }

        /// <summary>The document holding the compared elements: the open model, or the loaded link (null when unloaded).</summary>
        internal Document Doc
        {
            get
            {
                if (IsHost) return Host;
                try { return Instance.GetLinkDocument(); } catch { return null; }
            }
        }

        internal Transform ToHost => IsHost ? Transform.Identity : Instance.GetTotalTransform();

        internal Element Resolve(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            return Doc?.GetElement(uniqueId);
        }

        /// <summary>Host-side reference to a compared element (link reference for linked ones).</summary>
        internal Reference ReferenceTo(Element el)
        {
            if (el == null) return null;
            var r = new Reference(el);
            return IsHost ? r : r.CreateLinkReference(Instance);
        }

        internal string StableRepresentation(Element el)
        {
            if (el == null) return null;
            if (!IsHost) return LinkedElementRef.StableRepresentation(Host, Instance, el);
            try { return new Reference(el).ConvertToStableRepresentation(Host); }
            catch (Exception ex) { Log.Info($"[Federator] no stable reference for {el.UniqueId}: {ex.Message}"); return null; }
        }

        /// <summary>The compared element id a Revit selection reference points at, or -1 when it's something else.</summary>
        internal long IdOf(Reference r)
        {
            if (r == null) return -1;
            if (IsHost) return r.LinkedElementId == ElementId.InvalidElementId ? r.ElementId.GetIdValue() : -1;
            return r.LinkedElementId != ElementId.InvalidElementId && r.ElementId == Instance.Id ? r.LinkedElementId.GetIdValue() : -1;
        }

        /// <summary>
        /// federator/VersionCatalog identity of the open model, so snapshots of its saved copies show as cached (✓): the
        /// cloud model GUID, else the central file (workshared), else its own file, else its title.
        /// </summary>
        internal static string HostModelKey(Document doc)
        {
            if (VersionCatalog.TryCloudGuids(doc, out _, out Guid model)) return "cloud|" + model.ToString("N");
            try
            {
                if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    string p = central == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                    if (!string.IsNullOrEmpty(p) && !central.CloudPath) return "file|" + Path.GetFullPath(p).ToLowerInvariant();
                }
                if (!string.IsNullOrEmpty(doc.PathName) && !doc.IsModelInCloud) return "file|" + Path.GetFullPath(doc.PathName).ToLowerInvariant();
            }
            catch (Exception ex) { Log.Info($"[Federator] host model key: {ex.Message}"); }
            return "name|" + (doc.Title ?? "").ToLowerInvariant();
        }
    }
}
