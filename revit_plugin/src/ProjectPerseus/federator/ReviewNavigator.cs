using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using Element = Autodesk.Revit.DB.Element;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Takes the user to changed linked elements: selects the ones that still exist (link references via
    /// Selection.SetReferences) and zooms the active view to them. When they aren't visible in the active view, opens
    /// the user's own "Perseus Federator - &lt;user&gt;" 3D view with a section box around them (one view per user, so
    /// users never fight over its ownership). Deleted elements can't be selected; their old box is shown instead.
    /// Needs a Revit API context with no open transaction.
    /// </summary>
    internal static class ReviewNavigator
    {
        private const double MinPad = 1000 / 304.8;     // at least 1 m around the elements
        private const double SectionPad = 1500 / 304.8; // section box: 1.5 m around them
        internal const string ViewPrefix = "Perseus Federator - ";

        /// <summary>
        /// Selects the existing (not deleted) changed elements in Revit without zooming; deleted ones have nothing to
        /// select. Returns the linked element ids now selected (the panel ignores the SelectionChanged this causes).
        /// </summary>
        internal static HashSet<long> Select(UIDocument uidoc, RevitLinkInstance instance, IList<ElementChange> changes)
        {
            var ids = new HashSet<long>();
            if (uidoc == null || instance == null || changes == null) return ids;
            var refs = new List<Reference>();
            foreach (var c in changes)
            {
                if (c.Kind == ChangeKind.Deleted) continue;
                Element el = LinkedElementRef.Resolve(instance, c.UniqueId);
                if (el == null) continue;
                try { refs.Add(new Reference(el).CreateLinkReference(instance)); ids.Add(el.Id.GetIdValue()); }
                catch { /* not referenceable */ }
            }
            try { uidoc.Selection.SetReferences(refs); }
            catch (Exception ex) { Log.Info($"[Federator] couldn't select linked elements: {ex.Message}"); ids.Clear(); }
            return ids;
        }

        /// <summary>Returns a short message for the panel's status line.</summary>
        internal static string Show(UIDocument uidoc, RevitLinkInstance instance, IList<ElementChange> changes, bool select)
        {
            if (uidoc == null || instance == null || changes == null || changes.Count == 0) return "Nothing to show.";
            Document host = uidoc.Document;
            Transform toHost = instance.GetTotalTransform();

            XYZ min = null, max = null;
            foreach (var c in changes)
            {
                var bmin = c.Kind == ChangeKind.Deleted ? c.OldMin : c.NewMin ?? c.OldMin;
                var bmax = c.Kind == ChangeKind.Deleted ? c.OldMax : c.NewMax ?? c.OldMax;
                if (bmin == null || bmax == null) continue;
                HostBox(bmin, bmax, toHost, out XYZ hmin, out XYZ hmax);
                min = min == null ? hmin : new XYZ(Math.Min(min.X, hmin.X), Math.Min(min.Y, hmin.Y), Math.Min(min.Z, hmin.Z));
                max = max == null ? hmax : new XYZ(Math.Max(max.X, hmax.X), Math.Max(max.Y, hmax.Y), Math.Max(max.Z, hmax.Z));
                // Replaced: frame the old position too.
                if (c.Kind == ChangeKind.Replaced && c.OldMin != null && c.OldMax != null)
                {
                    HostBox(c.OldMin, c.OldMax, toHost, out XYZ omin, out XYZ omax);
                    min = new XYZ(Math.Min(min.X, omin.X), Math.Min(min.Y, omin.Y), Math.Min(min.Z, omin.Z));
                    max = new XYZ(Math.Max(max.X, omax.X), Math.Max(max.Y, omax.Y), Math.Max(max.Z, omax.Z));
                }
            }
            if (min == null) return "These elements have no location to show.";

            var existing = changes.Where(c => c.Kind != ChangeKind.Deleted).ToList();
            int selected = 0;
            if (select)
            {
                var refs = new List<Reference>();
                foreach (var c in existing)
                {
                    Element el = LinkedElementRef.Resolve(instance, c.UniqueId);
                    if (el == null) continue;
                    try { refs.Add(new Reference(el).CreateLinkReference(instance)); } catch { /* not referenceable */ }
                }
                try
                {
                    uidoc.Selection.SetReferences(refs);
                    selected = refs.Count;
                }
                catch (Exception ex) { Log.Info($"[Federator] couldn't select linked elements: {ex.Message}"); }
            }

            View view = uidoc.ActiveView;
            bool visible = IsVisible(host, view, instance, existing, min, max);
            string where;
            if (visible)
            {
                Zoom(uidoc, view, min, max);
                where = "in this view";
            }
            else
            {
                View3D v3 = EnsureFederatorView(host, uidoc.Application.Application.Username, min, max);
                if (v3 == null) return "Couldn't open the federator 3D view (see the log).";
                if (uidoc.ActiveView?.Id != v3.Id)
                {
                    try { uidoc.ActiveView = v3; }
                    catch { uidoc.RequestViewChange(v3); }
                }
                Zoom(uidoc, v3, min, max);
                where = $"in 3D view '{v3.Name}' (not visible in {view?.Name})";
            }

            int deleted = changes.Count - existing.Count;
            var parts = new List<string>();
            if (select) parts.Add($"Selected {selected}");
            parts.Add("shown " + where);
            if (deleted > 0) parts.Add($"{deleted} deleted shown by their old position");
            return string.Join(", ", parts) + ".";
        }

        /// <summary>
        /// Linked element ids (of this link instance) visible in the view; null when the view shows no model.
        ///
        /// Revit's linked-elements-in-view collector (FilteredElementCollector(host, view, link), 2024+) has crashed
        /// Revit with an access violation - uncatchable in .NET 8 - when called for a 3D view / a view not showing the
        /// link. So, like MarkerEngine: only for plan / section / elevation / detail views (IsProjectableView), and only
        /// once the link instance is confirmed visible in that view. Every other case uses element boxes against the
        /// view's extent (coarser, but safe).
        /// </summary>
        internal static HashSet<long> VisibleLinkedIds(Document host, View view, RevitLinkInstance instance)
        {
            var ids = new HashSet<long>();
            if (host == null || view == null || instance == null || view.IsTemplate) return null;
            if (!view.Document.Equals(host)) return null;
            var extent = ViewExtent.For(view);
            if (!extent.ShowsModel) return null;
            Document linkDoc = instance.GetLinkDocument();
            if (linkDoc == null) return ids;
            if (!LinkShownIn(host, view, instance)) return ids; // hidden link / link category off: nothing of it is visible
            try
            {
#if REVIT2024_OR_GREATER
                if (queue.ViolationScreenPositionEvent.IsProjectableView(view.ViewType))
                {
                    foreach (ElementId id in new FilteredElementCollector(host, view.Id, instance.Id).WhereElementIsNotElementType().ToElementIds())
                        ids.Add(id.GetIdValue());
                    return ids;
                }
#endif
                Transform toHost = instance.GetTotalTransform();
                foreach (Element el in new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb == null) continue;
                    HostBox(new[] { bb.Min.X, bb.Min.Y, bb.Min.Z }, new[] { bb.Max.X, bb.Max.Y, bb.Max.Z }, toHost, out XYZ a, out XYZ b);
                    if (extent.Contains(a, b)) ids.Add(el.Id.GetIdValue());
                }
            }
            catch (Exception ex) { Log.Info($"[Federator] visible linked elements in '{view.Name}': {ex.Message}"); }
            return ids;
        }

        /// <summary>Is the link instance itself visible in the view (ordinary view-scoped collector, safe in any view)?</summary>
        private static bool LinkShownIn(Document host, View view, RevitLinkInstance instance)
        {
            try
            {
                return new FilteredElementCollector(host, view.Id).OfClass(typeof(RevitLinkInstance))
                    .ToElementIds().Contains(instance.Id);
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] link visibility in '{view.Name}': {ex.Message}");
                return false;
            }
        }

        private static bool IsVisible(Document host, View view, RevitLinkInstance instance, List<ElementChange> existing, XYZ min, XYZ max)
        {
            if (view == null) return false;
            var extent = ViewExtent.For(view);
            if (!extent.ShowsModel || !extent.Contains(min, max)) return false;
            if (existing.Count == 0) return true; // deleted only: their old box is in view
            var visible = VisibleLinkedIds(host, view, instance);
            return visible != null && existing.Any(c => visible.Contains(c.Id));
        }

        private static void Zoom(UIDocument uidoc, View view, XYZ min, XYZ max)
        {
            try
            {
                UIView ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                if (ui == null) return;
                var size = max - min;
                double pad = Math.Max(MinPad, 0.25 * Math.Max(size.X, Math.Max(size.Y, size.Z)));
                var p = new XYZ(pad, pad, pad);
                ui.ZoomAndCenterRectangle(min - p, max + p);
            }
            catch (Exception ex) { Log.Info($"[Federator] zoom failed: {ex.Message}"); }
        }

        private static View3D EnsureFederatorView(Document doc, string user, XYZ min, XYZ max)
        {
            string name = ViewPrefix + (string.IsNullOrEmpty(user) ? Environment.UserName : user);
            try
            {
                View3D view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
                using (var tx = new Transaction(doc, "Perseus: federator 3D view"))
                {
                    tx.Start();
                    if (view == null)
                    {
                        var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
                        if (type == null) { tx.RollBack(); return null; }
                        view = View3D.CreateIsometric(doc, type.Id);
                        view.Name = name;
                        view.ViewTemplateId = ElementId.InvalidElementId;
                        view.DetailLevel = ViewDetailLevel.Fine;
                    }
                    var p = new XYZ(SectionPad, SectionPad, SectionPad);
                    view.SetSectionBox(new BoundingBoxXYZ { Min = min - p, Max = max + p });
                    view.IsSectionBoxActive = true;
                    tx.Commit();
                }
                return view;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Federator] federator 3D view '{name}': {ex.Message}");
                return null;
            }
        }

        internal static void HostBox(double[] min, double[] max, Transform toHost, out XYZ hmin, out XYZ hmax)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = toHost.OfPoint(new XYZ((i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2]));
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            hmin = new XYZ(x0, y0, z0);
            hmax = new XYZ(x1, y1, z1);
        }
    }
}
