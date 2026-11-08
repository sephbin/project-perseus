using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Registers ChangeHighlightServer at startup and turns the panel's changes into its items (host boxes via the
    /// link's LinkTransform). The panel calls Show with the rows it wants seen (filtered or selected) and then
    /// refreshes the active view in an API context.
    /// </summary>
    internal static class ChangeHighlightController
    {
        /// <summary>More than this and only the first ones (selected first) are drawn.</summary>
        internal const int MaxItems = 6000;

        internal static void Initialize()
        {
            try
            {
                var service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                if (service == null) { Log.Warn("[Federator] DirectContext3D service not available."); return; }
                service.AddServer(ChangeHighlightServer.Instance);
                var active = service.GetActiveServerIds();
                active.Add(ChangeHighlightServer.Instance.GetServerId());
                service.SetActiveServers(active);
            }
            catch (Exception ex) { Log.Warn($"[Federator] change highlight server not registered: {ex.Message}"); }
        }

        internal static bool Enabled
        {
            get => ChangeHighlightServer.Instance.Enabled;
            set => ChangeHighlightServer.Instance.Enabled = value;
        }

        /// <returns>How many changes are drawn (capped at MaxItems).</returns>
        internal static int Show(Document doc, IEnumerable<ElementChange> changes, LinkTransform toHost, ICollection<string> emphasised)
        {
            var items = new List<ChangeHighlightServer.Item>();
            int drawn = 0;
            var ordered = (changes ?? Enumerable.Empty<ElementChange>())
                .OrderByDescending(c => emphasised != null && emphasised.Contains(c.UniqueId));
            foreach (var c in ordered)
            {
                if (drawn >= MaxItems) break;
                bool emph = emphasised != null && emphasised.Contains(c.UniqueId);
                if (Add(items, c, toHost ?? LinkTransform.Identity, emph)) drawn++;
            }
            ChangeHighlightServer.Instance.Set(doc, items);
            return drawn;
        }

        internal static void Clear() => ChangeHighlightServer.Instance.Set(null, new List<ChangeHighlightServer.Item>());

        private static bool Add(List<ChangeHighlightServer.Item> items, ElementChange c, LinkTransform t, bool emph)
        {
            bool deleted = c.Kind == ChangeKind.Deleted;
            double[] min = deleted ? c.OldMin : c.NewMin, max = deleted ? c.OldMax : c.NewMax;
            if (min == null || max == null) return false;
            t.Box(min, max, out var hmin, out var hmax);
            var item = new ChangeHighlightServer.Item
            {
                Min = hmin,
                Max = hmax,
                Emphasis = emph,
                Kind = c.Kind == ChangeKind.Added ? ChangeHighlightServer.Kind.Added
                     : deleted ? ChangeHighlightServer.Kind.Deleted : ChangeHighlightServer.Kind.Modified,
            };
            if (c.Kind == ChangeKind.Modified && c.Aspects.HasFlag(ChangeAspect.Moved) && c.OldMin != null && c.OldMax != null)
            {
                t.Box(c.OldMin, c.OldMax, out var omin, out var omax);
                items.Add(new ChangeHighlightServer.Item { Min = omin, Max = omax, Kind = ChangeHighlightServer.Kind.Ghost, Emphasis = emph });
                item.From = Centre(omin, omax);
                item.To = Centre(hmin, hmax);
            }
            items.Add(item);
            return true;
        }

        private static double[] Centre(double[] a, double[] b) => new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2 };

        /// <summary>The link instance's transform as plain numbers (API context).</summary>
        internal static LinkTransform Capture(RevitLinkInstance instance)
        {
            if (instance == null) return LinkTransform.Identity;
            var t = instance.GetTotalTransform();
            return new LinkTransform
            {
                Origin = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z },
                BasisX = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z },
                BasisY = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z },
                BasisZ = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z },
            };
        }
    }
}
