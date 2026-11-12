using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Review views: host views registered with the federator (models/ReviewViewSettings, shared in the model) as the
    /// go-to views for showing changes. ReviewNavigator opens the first one that shows a change when the active view
    /// doesn't (before falling back to the federator 3D view); the panel assigns each change to one (ViewFor) and shows
    /// it on that view's captured image (federator/ViewImageCapture). API context.
    /// </summary>
    internal static class ReviewViews
    {
        internal const string Key = "review-views";

        private static readonly HashSet<ViewType> Usable = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan,
            ViewType.Section, ViewType.Elevation, ViewType.Detail, ViewType.ThreeD,
        };

        internal static ReviewViewSettings Load(Document doc) =>
            FederatorSettingsStore.Load<ReviewViewSettings>(doc, Key) ?? new ReviewViewSettings();

        internal static bool IsUsable(View v) =>
            v != null && !v.IsTemplate && Usable.Contains(v.ViewType) && !(v is View3D v3 && v3.IsPerspective);

        /// <summary>Views that can be review views (plans, sections, elevations, details, isometric 3D).</summary>
        internal static List<ViewCandidate> Candidates(Document doc)
        {
            var list = new List<ViewCandidate>();
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)))
            {
                if (!IsUsable(v)) continue;
                if (v.Name.StartsWith(ReviewNavigator.ViewPrefix, StringComparison.Ordinal)) continue;
                string sheet = "";
                try { sheet = v.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString() ?? ""; } catch { }
                if (sheet == "---") sheet = "";
                list.Add(new ViewCandidate { Uid = v.UniqueId, Name = v.Name, ViewType = v.ViewType.ToString(), SheetNumber = sheet, Scale = v.Scale });
            }
            return list.OrderBy(v => TypeOrder(v.ViewType)).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static int TypeOrder(string viewType)
        {
            switch (viewType)
            {
                case nameof(ViewType.FloorPlan): return 0;
                case nameof(ViewType.EngineeringPlan): return 1;
                case nameof(ViewType.CeilingPlan): return 2;
                case nameof(ViewType.AreaPlan): return 3;
                case nameof(ViewType.Section): return 4;
                case nameof(ViewType.Elevation): return 5;
                case nameof(ViewType.Detail): return 6;
                default: return 7;
            }
        }

        /// <summary>The registered views that still exist, in priority order.</summary>
        internal static List<View> Resolve(Document doc, ReviewViewSettings settings)
        {
            var list = new List<View>();
            if (doc == null || settings?.ViewUids == null) return list;
            foreach (var uid in settings.ViewUids)
            {
                View v = null;
                try { v = doc.GetElement(uid) as View; } catch { }
                if (IsUsable(v)) list.Add(v);
            }
            return list;
        }

        /// <summary>A review view with what it shows, for assigning changes to it.</summary>
        internal class Target
        {
            internal View View;
            internal ViewExtent Extent;
            internal bool LinkShown;
        }

        internal static List<Target> Targets(Document host, IEnumerable<View> views, RevitLinkInstance instance)
        {
            var list = new List<Target>();
            foreach (var v in views)
            {
                try
                {
                    var extent = ViewExtent.For(v);
                    if (!extent.ShowsModel) continue;
                    bool shown = instance == null || new FilteredElementCollector(host, v.Id).OfClass(typeof(RevitLinkInstance))
                                                         .ToElementIds().Contains(instance.Id);
                    list.Add(new Target { View = v, Extent = extent, LinkShown = shown });
                }
                catch (Exception ex) { Log.Info($"[Federator] review view '{v.Name}': {ex.Message}"); }
            }
            return list;
        }

        /// <summary>The first review view (priority order) showing this host box with the link visible; null when none.</summary>
        internal static Target ViewFor(IList<Target> targets, XYZ min, XYZ max, ElementId skip = null)
        {
            foreach (var t in targets)
            {
                if (!t.LinkShown || (skip != null && t.View.Id == skip)) continue;
                if (t.Extent.Contains(min, max)) return t;
            }
            return null;
        }
    }
}
