using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using Pt = ProjectPerseus.federator.OverlayShapes.Pt;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Draws the federator's changes in the model views (DirectContext3D: plans, sections, elevations, details and
    /// 3D; on screen only, never printed - baking does that, see AnnotationBaker), SVG-style: each change is a flat
    /// rounded rectangle on the view plane around the element's projected box, with a white halo, a coloured corner
    /// badge (+ added, − deleted, ~ modified) and, for moved elements, a dashed grey outline at the old position and an
    /// arrow from it to the new one. Solid green = added, solid amber = modified, dashed red = deleted (old position).
    /// Shapes come from OverlayShapes (2D). Placement (panel setting): AtElement (default) = on the element's own face
    /// nearest the camera, so things in front of it still cover it but it is never pushed up to the cut plane; OnTop =
    /// just behind the view's cut plane / near clip, over everything visible; Underneath = just behind each element,
    /// inside the view depth. Sizes are paper millimetres × view scale (like annotations); a 1 px line
    /// runs under every stroke so it never breaks up when zoomed out. Only small badges/arrowheads are filled, so the
    /// underlay stays visible. Batched per colour, cached per view until items, view direction, scale or clip change.
    /// </summary>
    internal class ChangeHighlightServer : IDirectContext3DServer
    {
        internal enum Kind : byte { Added, Modified, Deleted, Ghost }

        internal struct Item
        {
            internal double[] Min, Max;       // host coordinates (feet)
            internal Kind Kind;
            internal bool Emphasis;           // selected in the panel: heavier stroke, bigger badge
            internal double[] OldMin, OldMax; // moved elements: old box (host) → dashed ghost + arrow
        }

        internal static readonly ChangeHighlightServer Instance = new ChangeHighlightServer();
        private static readonly Guid ServerId = new Guid("5B0E7C51-3C8A-4E59-9E61-7B2F4E0D9A13");

        // Paper sizes (feet per unit of view scale).
        private const double Mm = 1 / 304.8;
        private const double StrokeHalf = 0.28 * Mm, StrokeHalfEmph = 0.5 * Mm, Halo = 0.35 * Mm;
        private const double Pad = 1.2 * Mm, MinRect = 3 * Mm, Radius = 1.5 * Mm;
        private const double DashLen = 1.8 * Mm, GapLen = 1.2 * Mm;
        private const double BadgeR = 1.7 * Mm, BadgeREmph = 2.1 * Mm, GlyphSize = 0.85 * Mm, GlyphHalf = 0.24 * Mm;
        private const double HeadLen = 2.6 * Mm, HeadHalf = 1.3 * Mm, ArrowGap = 0.8 * Mm, StartDot = 0.6 * Mm;
        private const double Lift = 1.0 * Mm, Layer = 0.3 * Mm;   // depth: off the element, between drawing layers
        private const double MinLiftFt = 15 / 304.8;

        private readonly object _lock = new object();
        private List<Item> _items = new List<Item>();
        private Document _doc;
        private int _version;
        private bool _loggedError;

        private readonly Dictionary<long, KeyValuePair<string, List<Batch>>> _cache = new Dictionary<long, KeyValuePair<string, List<Batch>>>();
        private readonly Dictionary<long, KeyValuePair<int, List<int>>> _visible = new Dictionary<long, KeyValuePair<int, List<int>>>();

        internal volatile bool Enabled = true;

        internal enum Placement { AtElement, OnTop, Underneath }
        /// <summary>Draw at the element's near face (AtElement), over everything visible (OnTop) or behind each changed
        /// element (Underneath).</summary>
        internal volatile Placement Place = Placement.AtElement;

        private ChangeHighlightServer() { }

        internal void Set(Document doc, List<Item> items)
        {
            lock (_lock)
            {
                _doc = doc;
                _items = items ?? new List<Item>();
                _version++;
            }
        }

        internal int Count { get { lock (_lock) return _items.Count; } }

        // ── IExternalServer ──
        public Guid GetServerId() => ServerId;
        public ExternalServiceId GetServiceId() => ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName() => "Perseus Federator Changes";
        public string GetVendorId() => "Perseus";
        public string GetDescription() => "Shows changes between versions of a linked model.";
        public string GetApplicationId() => ServerId.ToString();
        public string GetSourceId() => string.Empty;
        public bool UsesHandles() => false;

        // ── IDirectContext3DServer ──
        public bool CanExecute(View view)
        {
            try
            {
                if (!Enabled || view == null) return false;
                Document doc;
                lock (_lock) { if (_items.Count == 0) return false; doc = _doc; }
                if (doc == null || !doc.IsValidObject || !view.Document.Equals(doc)) return false;
                switch (view.ViewType)
                {
                    case ViewType.ThreeD:
                    case ViewType.FloorPlan:
                    case ViewType.CeilingPlan:
                    case ViewType.EngineeringPlan:
                    case ViewType.AreaPlan:
                    case ViewType.Section:
                    case ViewType.Elevation:
                    case ViewType.Detail:
                        return true;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }

        public Outline GetBoundingBox(View view) => null;
        // No translucent fills: faces drawn in the transparent pass hid the plan underlay drawn after them.
        public bool UseInTransparentPass(View view) => false;

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                List<Item> items;
                int version;
                lock (_lock) { items = _items; version = _version; }
                if (items.Count == 0) return;

                long viewKey = view.Id.GetIdValue();
                List<int> visible = VisibleFor(view, viewKey, items, version);
                if (visible.Count == 0) return;

                var frame = new Frame(view, Place);
                LogViewOnce(view, viewKey, version, frame, visible.Count);

                // Level of detail from the live camera: zoomed in = full shapes (only around the visible area when the
                // view holds too many to build at once), zoomed out / crowded = screen-sized dots with the glyph.
                var win = CameraWindow(frame);
                var lod = Choose(viewKey, version, frame, items, visible, win);
                string key = $"{version}|{frame.Key}|{lod.Key}";
                if (!_cache.TryGetValue(viewKey, out var cached) || cached.Key != key || !cached.Value.All(b => b.IsValid()))
                {
                    if (cached.Value != null) foreach (var b in cached.Value) b.Dispose();
                    var built = lod.Dots ? BuildDots(items, visible, frame, version, lod.Fpp) : Build(items, lod.Build, frame, version);
                    cached = new KeyValuePair<string, List<Batch>>(key, built);
                    _cache[viewKey] = cached;
                }
                foreach (var b in cached.Value)
                    DrawContext.FlushBuffer(b.Vb, b.Verts, b.Ib, b.Indices, b.Format, b.Effect, b.Primitive, 0, b.Prims);
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Warn($"[Federator] change highlight render failed: {ex}"); }
            }
        }

        // ── Level of detail ──

        // Zoomed out past this (badge radius on screen, px) or more changes than FullDetailLimit on screen: dots.
        private const double MinBadgePx = 5;
        private const double DotPx = 6.5, DotHaloPx = 1.6;   // dot radius / white rim, screen pixels
        private const double DotBucket = 1.25;               // dots are rebuilt when the zoom changes by this factor

        /// <summary>The visible part of the view in Frame (u, v) coordinates and feet per screen pixel.</summary>
        private struct Window
        {
            internal bool Ok;
            internal double U0, V0, U1, V1, Fpp;
            internal bool Overlaps(double[] r) => r[2] >= U0 && r[0] <= U1 && r[3] >= V0 && r[1] <= V1;
            internal bool Inside(double[] region) => U0 >= region[0] && U1 <= region[2] && V0 >= region[1] && V1 <= region[3];
        }

        private struct Lod
        {
            internal bool Dots;
            internal double Fpp;
            internal List<int> Build;
            internal string Key;
        }

        private readonly Dictionary<long, KeyValuePair<string, double[][]>> _rects = new Dictionary<long, KeyValuePair<string, double[][]>>();
        private readonly Dictionary<long, KeyValuePair<string, double[]>> _regions = new Dictionary<long, KeyValuePair<string, double[]>>();
        private readonly Dictionary<long, string> _loggedLod = new Dictionary<long, string>();

        // Orthographic camera only (perspective: no window; the old count-based style applies).
        private static Window CameraWindow(Frame f)
        {
            var w = new Window();
            try
            {
                var cam = DrawContext.GetCamera();
                if (cam == null || cam.ProjectionMethod != ProjectionMethod.Orthographic) return w;
                var vr = DrawContext.GetViewRectangle();
                double px = Math.Abs(vr.Right - vr.Left);
                double he = cam.HorizontalExtent, ve = cam.VerticalExtent;
                if (!(px > 0) || !(he > 0) || !(ve > 0) || double.IsInfinity(he) || double.IsInfinity(ve)) return w;
                var dir = cam.ViewDirection.Normalize();
                var up = cam.UpDirection.Normalize();
                var right = dir.CrossProduct(up);
                var c = cam.EyePosition + dir * cam.TargetDistance + right * cam.HorizontalOffset + up * cam.VerticalOffset;
                double cu = (c - f.O).DotProduct(f.R), cv = (c - f.O).DotProduct(f.U);
                double hu = (Math.Abs(right.DotProduct(f.R)) * he + Math.Abs(up.DotProduct(f.R)) * ve) / 2;
                double hv = (Math.Abs(right.DotProduct(f.U)) * he + Math.Abs(up.DotProduct(f.U)) * ve) / 2;
                w.U0 = cu - hu; w.U1 = cu + hu; w.V0 = cv - hv; w.V1 = cv + hv;
                w.Fpp = he / px;
                w.Ok = true;
            }
            catch { w.Ok = false; }
            return w;
        }

        // Projected rectangles of the visible changes, cached per view until the items or the frame change.
        private double[][] RectsFor(long viewKey, int version, Frame f, List<Item> items, List<int> visible)
        {
            string key = $"{version}|{f.Key}";
            if (_rects.TryGetValue(viewKey, out var c) && c.Key == key) return c.Value;
            var rects = new double[visible.Count][];
            for (int k = 0; k < visible.Count; k++)
            {
                f.Project(items[visible[k]].Min, items[visible[k]].Max, out var r, out _, out _);
                rects[k] = r;
            }
            _rects[viewKey] = new KeyValuePair<string, double[][]>(key, rects);
            return rects;
        }

        private Lod Choose(long viewKey, int version, Frame f, List<Item> items, List<int> visible, Window win)
        {
            var lod = new Lod { Build = visible, Key = "all" };
            if (!win.Ok) return lod;
            var rects = RectsFor(viewKey, version, f, items, visible);
            int inWin = 0;
            foreach (var r in rects) if (win.Overlaps(r)) inWin++;
            double badgePx = BadgeR * f.S / win.Fpp;
            string why;
            if (inWin > FullDetailLimit || badgePx < MinBadgePx)
            {
                lod.Dots = true;
                double step = Math.Round(Math.Log(win.Fpp) / Math.Log(DotBucket));
                lod.Fpp = Math.Pow(DotBucket, step);
                lod.Key = "dots" + step;
                why = $"dots ({inWin:N0} on screen, badge {badgePx:F1} px)";
            }
            else if (visible.Count > FullDetailLimit)
            {
                // Too many in the view to build at once: build around the window, rebuilt when the window leaves it.
                string prefix = $"{version}|{f.Key}|";
                if (_regions.TryGetValue(viewKey, out var reg) && reg.Key.StartsWith(prefix, StringComparison.Ordinal) && win.Inside(reg.Value))
                    lod.Key = reg.Key.Substring(prefix.Length);
                else
                {
                    double mu = (win.U1 - win.U0) * 0.5, mv = (win.V1 - win.V0) * 0.5;
                    var region = new[] { win.U0 - mu, win.V0 - mv, win.U1 + mu, win.V1 + mv };
                    lod.Key = $"region{region[0]:F1},{region[1]:F1},{region[2]:F1},{region[3]:F1}";
                    _regions[viewKey] = new KeyValuePair<string, double[]>(prefix + lod.Key, region);
                }
                var r0 = _regions[viewKey].Value;
                var near = new Window { Ok = true, U0 = r0[0], V0 = r0[1], U1 = r0[2], V1 = r0[3] };
                lod.Build = new List<int>();
                for (int k = 0; k < rects.Length; k++) if (near.Overlaps(rects[k])) lod.Build.Add(visible[k]);
                why = $"full, {lod.Build.Count:N0} near the screen";
            }
            else why = "full";

            string tier = lod.Dots ? "dots" : "full";
            if (!_loggedLod.TryGetValue(viewKey, out var last) || last != tier)
            {
                _loggedLod[viewKey] = tier;
                Log.Info($"[Federator] highlight detail: {why}; {inWin:N0} of {visible.Count:N0} in the window, {win.Fpp * 304.8:F1} mm/px");
            }
            return lod;
        }

        /// <summary>
        /// Zoomed-out style: every change is a dot of fixed screen size (white rim, colour, white + − ~ glyph) at the
        /// element's centre. Selected changes keep their full outline. Cheap enough to draw all of them.
        /// </summary>
        private List<Batch> BuildDots(List<Item> items, List<int> visible, Frame f, int version, double fpp)
        {
            var layers = new Dictionary<Kind, Layer3D>();
            var white = new Layer3D();
            Layer3D LayerOf(Kind k) { if (!layers.TryGetValue(k, out var l)) layers[k] = l = new Layer3D(); return l; }
            double eps = Layer * f.S, r = DotPx * fpp, rim = DotHaloPx * fpp;
            var emphasised = new List<int>();
            var t = new List<double>();
            foreach (int i in visible)
            {
                var it = items[i];
                if (it.Emphasis) { emphasised.Add(i); continue; }
                f.Project(it.Min, it.Max, out var rect, out double wNear, out double wFar);
                double w = f.Depth(wNear, wFar);
                var c = Centre(rect);
                t.Clear(); OverlayShapes.Disc(t, c, r + rim, 20); white.AddTris(f, t, w - eps);
                t.Clear(); OverlayShapes.Disc(t, c, r, 20); LayerOf(it.Kind).AddTris(f, t, w + eps);
                t.Clear(); OverlayShapes.Glyph(t, GlyphOf(it.Kind), c, r * 0.5, r * 0.14); white.AddTris(f, t, w + 2 * eps);
            }
            var batches = new List<Batch>();
            AddBatches(batches, white, new Color(255, 255, 255), lines: false);
            foreach (var kv in layers) AddBatches(batches, kv.Value, ColorOf(kv.Key), lines: false);
            if (emphasised.Count > 0) batches.AddRange(Build(items, emphasised, f, version));
            return batches;
        }

        private List<int> VisibleFor(View view, long viewKey, List<Item> items, int version)
        {
            if (_visible.TryGetValue(viewKey, out var v) && v.Key == version) return v.Value;
            var extent = ViewExtent.For(view);
            var list = new List<int>();
            for (int i = 0; i < items.Count; i++)
                if (extent.Contains(P(items[i].Min), P(items[i].Max))) list.Add(i);
            _visible[viewKey] = new KeyValuePair<int, List<int>>(version, list);
            if (list.Count == 0)
            {
                // Diagnose "nothing shows in this view": what the view covers vs where the changes are (host feet).
                double zLo = items.Min(it => it.Min[2]), zHi = items.Max(it => it.Max[2]);
                var sample = items.Take(3).Select(it => $"({it.Min[0]:F1},{it.Min[1]:F1},{it.Min[2]:F2})..({it.Max[0]:F1},{it.Max[1]:F1},{it.Max[2]:F2})");
                Log.Info($"[Federator] highlight '{view.Name}' ({view.ViewType}): none of {items.Count} change(s) in view. " +
                         $"View covers {extent.Describe()}; changes span z {zLo:F2}..{zHi:F2}, e.g. {string.Join(" ", sample)}");
            }
            return list;
        }

        // ── View frame: screen axes, scale and the depth limit (cut plane / near clip) ──

        private class Frame
        {
            internal readonly XYZ O, R, U, N;
            internal readonly XYZ ReportedN;   // view.ViewDirection as Revit gave it (diagnostics)
            internal readonly double S;          // view scale
            internal readonly double? NearW;     // shapes must stay behind this depth (w grows toward the viewer)
            internal readonly double? FarW;      // ... and in front of this one (view depth / far clip)
            internal readonly double? EyeW;      // orthographic 3D without section box: camera depth
            internal readonly Placement Place;
            internal readonly string Key;

            internal Frame(View view, Placement place)
            {
                O = view.Origin; R = view.RightDirection; U = view.UpDirection; N = view.ViewDirection;
                ReportedN = N;
                // Plans: the toward-the-viewer axis is fixed - up for floor / engineering / area plans, DOWN for reflected
                // ceiling plans (you look up). ViewDirection isn't trusted for RCPs: with the wrong sign the white halo
                // stacks in front of the coloured stroke (invisible on white) and "On top" lands behind the ceilings.
                if (view.ViewType == ViewType.CeilingPlan) N = new XYZ(0, 0, -1);
                else if (view is ViewPlan) N = XYZ.BasisZ;
                S = Math.Max(1, view.Scale);
                Place = place;
                Clip(view, out NearW, out FarW);
                if (view is View3D v3 && !v3.IsPerspective && !NearW.HasValue)
                {
                    try { EyeW = Wof(v3.GetOrientation().EyePosition); } catch { EyeW = null; }
                }
                Key = $"{N.X:F3},{N.Y:F3},{N.Z:F3}|{R.X:F3},{R.Y:F3}|{S}|{place}|{F(NearW)}|{F(FarW)}|{F(EyeW)}";
            }

            private static string F(double? d) => d.HasValue ? d.Value.ToString("F3") : "-";

            internal double Wof(XYZ p) => (p - O).DotProduct(N);
            internal XYZ At(double u, double v, double w) => O + R * u + U * v + N * w;

            // Rectangle (u0, v0, u1, v1) and depth range of a host box on this view.
            internal void Project(double[] min, double[] max, out double[] rect, out double wNear, out double wFar)
            {
                double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
                wNear = double.MinValue;
                wFar = double.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    var p = new XYZ((i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2]) - O;
                    double u = p.DotProduct(R), v = p.DotProduct(U), w = p.DotProduct(N);
                    u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                    v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
                    wNear = Math.Max(wNear, w);
                    wFar = Math.Min(wFar, w);
                }
                rect = new[] { u0, v0, u1, v1 };
            }

            /// <summary>
            /// Base depth for an element's shapes (they occupy base − Layer … base + 2·Layer). At element: on the face of
            /// the element's box nearest the camera (lifted just off it against z-fighting), so whatever is in front of the
            /// element in the view still covers the shapes; kept behind the cut plane / near clip and in front of the view
            /// depth like the others. On top: just behind the
            /// cut plane / near clip / section box front, so nothing visible covers them (orthographic 3D without a
            /// section box: just in front of the camera; perspective: in front of the element). Underneath: just behind
            /// the element, so its own linework draws over them, but in front of the view depth so they aren't clipped.
            /// </summary>
            internal double Depth(double wNear, double wFar)
            {
                double lift = Math.Max(MinLiftFt, Lift * S), layers = 3 * Layer * S;
                double w;
                if (Place == Placement.Underneath)
                {
                    w = wFar - lift - layers;
                    if (FarW.HasValue) w = Math.Max(w, FarW.Value + lift + layers);
                }
                else if (Place == Placement.AtElement)
                {
                    w = wNear + lift;
                    if (FarW.HasValue) w = Math.Max(w, FarW.Value + lift + layers);
                }
                else
                {
                    if (NearW.HasValue) w = NearW.Value - lift - layers;
                    else if (EyeW.HasValue) w = Math.Max(wNear + lift, EyeW.Value - Math.Max(1.0, 10 * lift));
                    else w = wNear + lift;
                }
                if (NearW.HasValue) w = Math.Min(w, NearW.Value - lift - layers);
                return w;
            }

            // Near / far depth limits: plan cut plane / view depth plane; section, elevation, detail crop box; 3D section box.
            private void Clip(View view, out double? near, out double? far)
            {
                near = far = null;
                try
                {
                    if (view is ViewPlan plan && plan.GenLevel != null)
                    {
                        var vr = plan.GetViewRange();
                        near = Wof(new XYZ(O.X, O.Y, PlaneZ(view.Document, plan, vr, PlanViewPlane.CutPlane)));
                        double depthZ = PlaneZ(view.Document, plan, vr, PlanViewPlane.ViewDepthPlane);
                        if (!double.IsNaN(depthZ)) far = Wof(new XYZ(O.X, O.Y, depthZ));
                        if (far.HasValue && near.HasValue && far.Value > near.Value) { var t0 = near; near = far; far = t0; }
                        return;
                    }
                    BoundingBoxXYZ box = null;
                    if (view is View3D v3) { if (v3.IsSectionBoxActive) box = v3.GetSectionBox(); }
                    else if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail)
                        box = view.CropBox;
                    if (box == null) return;
                    var t = box.Transform ?? Transform.Identity;
                    double hi = double.MinValue, lo = double.MaxValue;
                    for (int i = 0; i < 8; i++)
                    {
                        double w = Wof(t.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                                                         (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                         (i & 4) == 0 ? box.Min.Z : box.Max.Z)));
                        hi = Math.Max(hi, w); lo = Math.Min(lo, w);
                    }
                    near = hi;
                    far = lo;
                }
                catch { near = far = null; }
            }

            private static double PlaneZ(Document doc, ViewPlan plan, PlanViewRange vr, PlanViewPlane plane)
            {
                var id = vr.GetLevelId(plane);
                var level = id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) as Level : null;
                if (level == null)
                {
                    if (plane == PlanViewPlane.ViewDepthPlane) return double.NaN; // unlimited
                    level = plan.GenLevel;
                }
                return level.ProjectElevation + vr.GetOffset(plane);
            }
        }

        // ── Building the shapes ──

        private static Color ColorOf(Kind k)
        {
            switch (k)
            {
                case Kind.Added: return new Color(46, 173, 75);
                case Kind.Deleted: return new Color(210, 58, 58);
                case Kind.Ghost: return new Color(128, 134, 145);
                default: return new Color(224, 130, 0);
            }
        }

        private static char GlyphOf(Kind k) => k == Kind.Added ? '+' : k == Kind.Deleted ? '-' : '~';

        /// <summary>Triangles + 1 px lines for one colour, at their depths.</summary>
        private class Layer3D
        {
            internal readonly List<XYZ> Tris = new List<XYZ>();
            internal readonly List<XYZ[]> Lines = new List<XYZ[]>();

            internal void AddTris(Frame f, List<double> uv, double w)
            {
                for (int i = 0; i + 1 < uv.Count; i += 2) Tris.Add(f.At(uv[i], uv[i + 1], w));
            }

            internal void AddLine(Frame f, IList<Pt> line, bool closed, double w)
            {
                int n = closed ? line.Count : line.Count - 1;
                for (int i = 0; i < n; i++)
                {
                    var a = line[i]; var b = line[(i + 1) % line.Count];
                    Lines.Add(new[] { f.At(a.U, a.V, w), f.At(b.U, b.V, w) });
                }
            }
        }

        // More changes than this in a view: simple style (square, no halo / glyph, solid lines) to keep the build cheap.
        private const int FullDetailLimit = 800;
        // Hard ceiling per view build; past it the remaining changes aren't drawn (logged once per version).
        private const int MaxTrianglesPerView = 600000;
        private int _budgetLoggedVersion = -1;
        private readonly Dictionary<long, int> _loggedViews = new Dictionary<long, int>();

        // One line per view per set of items: what the renderer saw (view type, directions, clip depths, how many
        // changes are in view) - enough to diagnose "nothing shows in this view" from the log.
        private void LogViewOnce(View view, long viewKey, int version, Frame f, int visible)
        {
            if (_loggedViews.TryGetValue(viewKey, out int v) && v == version) return;
            _loggedViews[viewKey] = version;
            string V(XYZ p) => $"({p.X:F2},{p.Y:F2},{p.Z:F2})";
            string D(double? d) => d.HasValue ? d.Value.ToString("F2") : "-";
            Log.Info($"[Federator] highlight '{view.Name}' ({view.ViewType}, 1:{f.S}): viewDir {V(f.ReportedN)} -> using {V(f.N)}, " +
                     $"right {V(f.R)}, near {D(f.NearW)}, far {D(f.FarW)}, eye {D(f.EyeW)}, place {f.Place}, {visible} change(s) in view");
        }

        private List<Batch> Build(List<Item> items, List<int> visible, Frame f, int version)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var layers = new Dictionary<Kind, Layer3D>();
            var white = new Layer3D();
            Layer3D LayerOf(Kind k) { if (!layers.TryGetValue(k, out var l)) layers[k] = l = new Layer3D(); return l; }
            double s = f.S, eps = Layer * s;
            bool simple = visible.Count > FullDetailLimit;
            int drawn = 0;

            foreach (int i in visible)
            {
                if ((white.Tris.Count + layers.Values.Sum(l => l.Tris.Count)) / 3 > MaxTrianglesPerView)
                {
                    if (_budgetLoggedVersion != version)
                    {
                        _budgetLoggedVersion = version;
                        Log.Info($"[Federator] highlight budget reached: drew {drawn:N0} of {visible.Count:N0} changes in this view");
                    }
                    break;
                }
                drawn++;
                var it = items[i];
                f.Project(it.Min, it.Max, out var rect, out double wNear, out double wFar);
                double w = f.Depth(wNear, wFar);
                Grow(rect, Pad * s, MinRect * s);
                bool dashed = it.Kind == Kind.Deleted && !simple;
                double half = (it.Emphasis ? StrokeHalfEmph : StrokeHalf) * s;
                Outline(f, rect, dashed, half, w, eps, LayerOf(it.Kind), simple && !it.Emphasis ? null : white, simple);

                // Moved: dashed ghost at the old position + arrow old → new.
                var badgeAt = new Pt(rect[0], rect[3]);
                if (it.OldMin != null && it.OldMax != null)
                {
                    f.Project(it.OldMin, it.OldMax, out var oldRect, out double wOld, out double wOldFar);
                    double wg = f.Depth(wOld, wOldFar);
                    Grow(oldRect, Pad * s, MinRect * s);
                    Outline(f, oldRect, !simple, StrokeHalf * s, wg, eps, LayerOf(Kind.Ghost), simple ? null : white, simple);

                    var a = Centre(oldRect); var b = Centre(rect);
                    if (!OverlayShapes.ClipBetween(a, b, oldRect, rect, ArrowGap * s, out var from, out var to)) { from = a; to = b; }
                    var topRight = new Pt(rect[2], rect[3]);
                    if ((to - badgeAt).Length < (to - topRight).Length) badgeAt = topRight;
                    if ((to - from).Length > HeadLen * s * 0.5)
                    {
                        double aw = f.Place == Placement.Underneath ? Math.Min(w, wg) : Math.Max(w, wg);
                        var arrow = new List<double>();
                        if (!simple)
                        {
                            OverlayShapes.Arrow(arrow, from, to, half + Halo * s, (HeadLen + 0.7 * Mm) * s, (HeadHalf + 0.5 * Mm) * s);
                            OverlayShapes.Disc(arrow, from, (StartDot + Halo) * s, 20);
                            white.AddTris(f, arrow, aw - eps);
                            arrow.Clear();
                        }
                        OverlayShapes.Arrow(arrow, from, to, half, HeadLen * s, HeadHalf * s);
                        if (!simple) OverlayShapes.Disc(arrow, from, StartDot * s, 20);
                        LayerOf(Kind.Modified).AddTris(f, arrow, aw);
                        LayerOf(Kind.Modified).AddLine(f, new[] { from, to }, false, aw);
                    }
                }

                // Badge on a top corner: the left one, unless the move arrow ends nearer to it.
                double br = (it.Emphasis ? BadgeREmph : BadgeR) * s;
                var t = new List<double>();
                if (simple && !it.Emphasis)
                {
                    // Simple style keeps the badge and its white glyph (cheap); only the halos are dropped.
                    OverlayShapes.Disc(t, badgeAt, br, 24);
                    LayerOf(it.Kind).AddTris(f, t, w + eps);
                    t.Clear();
                    OverlayShapes.Glyph(t, GlyphOf(it.Kind), badgeAt, GlyphSize * s, GlyphHalf * s);
                    white.AddTris(f, t, w + 2 * eps);
                    continue;
                }
                OverlayShapes.Disc(t, badgeAt, br + Halo * s, 40);
                white.AddTris(f, t, w - eps);
                t.Clear();
                OverlayShapes.Disc(t, badgeAt, br, 40);
                LayerOf(it.Kind).AddTris(f, t, w + eps);
                t.Clear();
                OverlayShapes.Glyph(t, GlyphOf(it.Kind), badgeAt, GlyphSize * s * (br / (BadgeR * s)), GlyphHalf * s);
                white.AddTris(f, t, w + 2 * eps);
            }

            var batches = new List<Batch>();
            // Halo first (it's behind by depth anyway), then colours.
            AddBatches(batches, white, new Color(255, 255, 255), lines: false);
            foreach (var kv in layers) AddBatches(batches, kv.Value, ColorOf(kv.Key), lines: true);
            if (clock.ElapsedMilliseconds > 250)
                Log.Info($"[Federator] highlight build took {clock.ElapsedMilliseconds} ms for {drawn:N0} changes ({(simple ? "simple" : "full")} style)");
            return batches;
        }

        private static void Outline(Frame f, double[] rect, bool dashed, double half, double w, double eps, Layer3D colour, Layer3D white,
                                    bool simple)
        {
            double s = f.S;
            var path = OverlayShapes.RoundedRect(rect[0], rect[1], rect[2], rect[3], simple ? 0 : Radius * s, 10);
            var halo = new List<double>();
            var stroke = new List<double>();
            if (dashed)
            {
                foreach (var dash in OverlayShapes.Dash(path, true, DashLen * s, GapLen * s))
                {
                    if (white != null) OverlayShapes.Stroke(halo, dash, false, half + Halo * s);
                    OverlayShapes.Stroke(stroke, dash, false, half);
                    colour.AddLine(f, dash, false, w);
                }
            }
            else
            {
                if (white != null) OverlayShapes.Stroke(halo, path, true, half + Halo * s);
                OverlayShapes.Stroke(stroke, path, true, half);
                colour.AddLine(f, path, true, w);
            }
            if (white != null) white.AddTris(f, halo, w - eps);
            colour.AddTris(f, stroke, w);
        }

        private static void Grow(double[] r, double pad, double min)
        {
            r[0] -= pad; r[1] -= pad; r[2] += pad; r[3] += pad;
            if (r[2] - r[0] < min) { double c = (r[0] + r[2]) / 2; r[0] = c - min / 2; r[2] = c + min / 2; }
            if (r[3] - r[1] < min) { double c = (r[1] + r[3]) / 2; r[1] = c - min / 2; r[3] = c + min / 2; }
        }

        private static Pt Centre(double[] r) => new Pt((r[0] + r[2]) / 2, (r[1] + r[3]) / 2);

        private static void AddBatches(List<Batch> batches, Layer3D layer, Color colour, bool lines)
        {
            var t = Batch.FromTriangles(layer.Tris, colour);
            if (t != null) batches.Add(t);
            if (lines)
            {
                var l = Batch.FromLines(layer.Lines, colour);
                if (l != null) batches.Add(l);
            }
        }

        private static XYZ P(double[] p) => new XYZ(p[0], p[1], p[2]);

        /// <summary>One colour's vertex + index buffers, ready to flush.</summary>
        private class Batch : IDisposable
        {
            private const int MaxVerts = 3000000;

            internal VertexBuffer Vb;
            internal IndexBuffer Ib;
            internal VertexFormat Format;
            internal EffectInstance Effect;
            internal int Verts, Indices, Prims;
            internal PrimitiveType Primitive = PrimitiveType.TriangleList;

            internal bool IsValid() => Vb.IsValid() && Ib.IsValid() && Format.IsValid() && Effect.IsValid();

            public void Dispose()
            {
                try { Vb?.Dispose(); Ib?.Dispose(); Format?.Dispose(); Effect?.Dispose(); } catch { /* already gone */ }
            }

            // Flat triangles (3 vertices each), both windings so they show whichever way the view faces.
            internal static Batch FromTriangles(List<XYZ> verts, Color color)
            {
                int tris = verts.Count / 3;
                if (tris == 0 || verts.Count > MaxVerts) return null;
                var b = new Batch();
                int floats = verts.Count * VertexPosition.GetSizeInFloats();
                b.Vb = new VertexBuffer(floats);
                b.Vb.Map(floats);
                var vs = b.Vb.GetVertexStreamPosition();
                foreach (var p in verts) vs.AddVertex(new VertexPosition(p));
                int idx = tris * 2 * IndexTriangle.GetSizeInShortInts();
                b.Ib = new IndexBuffer(idx);
                b.Ib.Map(idx);
                var ist = b.Ib.GetIndexStreamTriangle();
                for (int k = 0; k < tris; k++)
                {
                    int o = k * 3;
                    ist.AddTriangle(new IndexTriangle(o, o + 1, o + 2));
                    ist.AddTriangle(new IndexTriangle(o, o + 2, o + 1));
                }
                b.Verts = verts.Count;
                b.Prims = tris * 2;
                b.Indices = idx;
                return b.Finish(color);
            }

            internal static Batch FromLines(List<XYZ[]> lines, Color color)
            {
                if (lines.Count == 0 || lines.Count * 2 > MaxVerts) return null;
                var b = new Batch { Primitive = PrimitiveType.LineList };
                int verts = lines.Count * 2;
                int floats = verts * VertexPosition.GetSizeInFloats();
                b.Vb = new VertexBuffer(floats);
                b.Vb.Map(floats);
                var vs = b.Vb.GetVertexStreamPosition();
                int idx = lines.Count * IndexLine.GetSizeInShortInts();
                b.Ib = new IndexBuffer(idx);
                b.Ib.Map(idx);
                var ils = b.Ib.GetIndexStreamLine();
                for (int k = 0; k < lines.Count; k++)
                {
                    vs.AddVertex(new VertexPosition(lines[k][0]));
                    vs.AddVertex(new VertexPosition(lines[k][1]));
                    ils.AddLine(new IndexLine(k * 2, k * 2 + 1));
                }
                b.Verts = verts;
                b.Prims = lines.Count;
                b.Indices = idx;
                return b.Finish(color);
            }

            private Batch Finish(Color color)
            {
                Vb.Unmap();
                Ib.Unmap();
                Format = new VertexFormat(VertexFormatBits.Position);
                Effect = new EffectInstance(VertexFormatBits.Position);
                Effect.SetColor(color);
                Effect.SetEmissiveColor(color);
                return this;
            }
        }
    }
}
