using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using ProjectPerseus.logging;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Draws the federator's changes in the model views (DirectContext3D: plans, sections, elevations, details and
    /// 3D; on screen only, never printed - baking does that, see AnnotationBaker). Each change is a box in host
    /// coordinates: green added, amber modified, red deleted (at its old position); a moved element also gets a grey
    /// ghost of its old box and a connector from old to new. Edges are view-facing quads (DirectContext3D lines are
    /// 1 px) sized from the view scale, plus 1 px lines so thin edges never break up; no face fill (it hid the plan
    /// underlay). Geometry is
    /// batched per colour and cached per view until the items, view direction or scale change.
    /// </summary>
    internal class ChangeHighlightServer : IDirectContext3DServer
    {
        internal enum Kind : byte { Added, Modified, Deleted, Ghost }

        internal struct Item
        {
            internal double[] Min, Max;   // host coordinates (feet)
            internal Kind Kind;
            internal bool Emphasis;       // selected in the panel: wider edges, stronger fill
            internal double[] From, To;   // move connector (old centre → new centre), or null
        }

        internal static readonly ChangeHighlightServer Instance = new ChangeHighlightServer();
        private static readonly Guid ServerId = new Guid("5B0E7C51-3C8A-4E59-9E61-7B2F4E0D9A13");

        private const double EdgePaperFt = 0.35 / 304.8;   // edge half-width: 0.35 mm on paper
        private const double MinHalfFt = 2 / 304.8;
        // Boxes are pushed this far outside the element's own bounding box so their faces never sit in the same plane
        // as the element's faces (coplanar faces z-fight: the fill turns into shimmering pixel noise).
        private const double OffsetPaperFt = 1.0 / 304.8;
        private const double MinOffsetFt = 15 / 304.8;

        private readonly object _lock = new object();
        private List<Item> _items = new List<Item>();
        private Document _doc;
        private int _version;
        private bool _loggedError;

        // view id → cached batches for that view's current key
        private readonly Dictionary<long, KeyValuePair<string, List<Batch>>> _cache = new Dictionary<long, KeyValuePair<string, List<Batch>>>();
        private readonly Dictionary<long, KeyValuePair<int, List<int>>> _visible = new Dictionary<long, KeyValuePair<int, List<int>>>();

        internal volatile bool Enabled = true;

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
        // No translucent fill: faces drawn in the transparent pass still hide what Revit draws after them (the plan
        // underlay vanished behind every box). Edges only; selected rows are told apart by wider edges.
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

                bool transparent = DrawContext.IsTransparentPass();
                double scale = Math.Max(1, view.Scale);
                double half = Math.Max(MinHalfFt, EdgePaperFt * scale);
                double offset = Math.Max(MinOffsetFt, OffsetPaperFt * scale);
                XYZ dir = view.ViewDirection;
                string key = $"{version}|{transparent}|{dir.X:F3},{dir.Y:F3},{dir.Z:F3}|{half:F5}";

                if (!_cache.TryGetValue(viewKey * 2 + (transparent ? 1 : 0), out var cached) || cached.Key != key || !cached.Value.All(b => b.IsValid()))
                {
                    if (cached.Value != null) foreach (var b in cached.Value) b.Dispose();
                    var batches = transparent ? BuildFaces(items, visible, offset) : BuildEdges(items, visible, dir, half, offset);
                    cached = new KeyValuePair<string, List<Batch>>(key, batches);
                    _cache[viewKey * 2 + (transparent ? 1 : 0)] = cached;
                }
                foreach (var b in cached.Value)
                    DrawContext.FlushBuffer(b.Vb, b.Verts, b.Ib, b.Indices, b.Format, b.Effect, b.Primitive, 0, b.Prims);
            }
            catch (Exception ex)
            {
                if (!_loggedError) { _loggedError = true; Log.Warn($"[Federator] change highlight render failed: {ex.Message}"); }
            }
        }

        private List<int> VisibleFor(View view, long viewKey, List<Item> items, int version)
        {
            if (_visible.TryGetValue(viewKey, out var v) && v.Key == version) return v.Value;
            var extent = ViewExtent.For(view);
            var list = new List<int>();
            for (int i = 0; i < items.Count; i++)
                if (extent.Contains(P(items[i].Min), P(items[i].Max))) list.Add(i);
            _visible[viewKey] = new KeyValuePair<int, List<int>>(version, list);
            return list;
        }

        // ── Geometry ──

        private static Color ColorOf(Kind k)
        {
            switch (k)
            {
                case Kind.Added: return new Color(46, 173, 75);
                case Kind.Deleted: return new Color(210, 58, 58);
                case Kind.Ghost: return new Color(140, 140, 140);
                default: return new Color(224, 130, 0);
            }
        }

        private static readonly int[] EdgeA = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
        private static readonly int[] EdgeB = { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };

        private static XYZ[] Corners(Item it, double o)
        {
            double x0 = it.Min[0] - o, y0 = it.Min[1] - o, z0 = it.Min[2] - o;
            double x1 = it.Max[0] + o, y1 = it.Max[1] + o, z1 = it.Max[2] + o;
            return new[]
            {
                new XYZ(x0, y0, z0), new XYZ(x1, y0, z0), new XYZ(x1, y1, z0), new XYZ(x0, y1, z0),
                new XYZ(x0, y0, z1), new XYZ(x1, y0, z1), new XYZ(x1, y1, z1), new XYZ(x0, y1, z1),
            };
        }

        // Thick edges are view-facing quads; when zoomed out they get thinner than a pixel and break up into jagged
        // dashes, so every edge is also drawn as a 1 px line, which the GPU always rasterises as a continuous line.
        private List<Batch> BuildEdges(List<Item> items, List<int> visible, XYZ viewDir, double half, double offset)
        {
            var batches = new List<Batch>();
            foreach (var group in visible.GroupBy(i => items[i].Kind))
            {
                var quads = new List<XYZ[]>();
                var lines = new List<XYZ[]>();
                foreach (int i in group)
                {
                    var it = items[i];
                    double w = it.Emphasis ? half * 2.2 : half;
                    var c = Corners(it, offset);
                    for (int e = 0; e < 12; e++)
                    {
                        AddQuad(quads, c[EdgeA[e]], c[EdgeB[e]], viewDir, w);
                        lines.Add(new[] { c[EdgeA[e]], c[EdgeB[e]] });
                    }
                    if (it.From != null && it.To != null)
                    {
                        AddQuad(quads, P(it.From), P(it.To), viewDir, w * 0.8);
                        lines.Add(new[] { P(it.From), P(it.To) });
                    }
                }
                var colour = ColorOf(group.Key);
                var l = Batch.FromLines(lines, colour);
                if (l != null) batches.Add(l);
                var b = Batch.FromQuads(quads, colour, 0);
                if (b != null) batches.Add(b);
            }
            return batches;
        }

        private List<Batch> BuildFaces(List<Item> items, List<int> visible, double offset)
        {
            var batches = new List<Batch>();
            foreach (var group in visible.Where(i => items[i].Kind != Kind.Ghost).GroupBy(i => new { items[i].Kind, items[i].Emphasis }))
            {
                var boxes = group.Select(i => Corners(items[i], offset)).ToList();
                var b = Batch.FromBoxes(boxes, ColorOf(group.Key.Kind), group.Key.Emphasis ? 0.55 : 0.82);
                if (b != null) batches.Add(b);
            }
            return batches;
        }

        private static void AddQuad(List<XYZ[]> quads, XYZ a, XYZ b, XYZ viewDir, double half)
        {
            XYZ v = b - a;
            double len = v.GetLength();
            if (len < 1e-9) return;
            XYZ d = v / len;
            XYZ perp = d.CrossProduct(viewDir);
            if (perp.GetLength() < 1e-6) perp = d.CrossProduct(Math.Abs(d.X) < 0.9 ? XYZ.BasisX : XYZ.BasisY);
            double pl = perp.GetLength();
            if (pl < 1e-9) return;
            perp = perp * (half / pl);
            quads.Add(new[] { a + perp, a - perp, b + perp, b - perp });
        }

        private static XYZ P(double[] p) => new XYZ(p[0], p[1], p[2]);

        /// <summary>One colour's vertex + index buffers, ready to flush.</summary>
        private class Batch : IDisposable
        {
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

            // Quads (4 corners: a+p, a−p, b+p, b−p), both windings so they show from either side.
            internal static Batch FromQuads(List<XYZ[]> quads, Color color, double transparency)
            {
                if (quads.Count == 0) return null;
                int verts = quads.Count * 4, tris = quads.Count * 4;
                var b = Begin(verts, tris, out var vs, out var ist);
                for (int q = 0; q < quads.Count; q++)
                {
                    foreach (var p in quads[q]) vs.AddVertex(new VertexPosition(p));
                    int v = q * 4;
                    ist.AddTriangle(new IndexTriangle(v, v + 2, v + 1));
                    ist.AddTriangle(new IndexTriangle(v + 1, v + 2, v + 3));
                    ist.AddTriangle(new IndexTriangle(v, v + 1, v + 2));
                    ist.AddTriangle(new IndexTriangle(v + 1, v + 3, v + 2));
                }
                return b.End(verts, tris, color, transparency);
            }

            internal static Batch FromLines(List<XYZ[]> lines, Color color)
            {
                if (lines.Count == 0) return null;
                int verts = lines.Count * 2;
                var b = new Batch { Primitive = PrimitiveType.LineList };
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
                b.Vb.Unmap();
                b.Ib.Unmap();
                b.Verts = verts;
                b.Prims = lines.Count;
                b.Indices = idx;
                b.Format = new VertexFormat(VertexFormatBits.Position);
                b.Effect = new EffectInstance(VertexFormatBits.Position);
                b.Effect.SetColor(color);
                b.Effect.SetEmissiveColor(color);
                return b;
            }

            private static readonly int[][] Faces =
            {
                new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 },
            };

            // Boxes (8 corners), 6 faces × 2 triangles × 2 windings.
            internal static Batch FromBoxes(List<XYZ[]> boxes, Color color, double transparency)
            {
                if (boxes.Count == 0) return null;
                int verts = boxes.Count * 8, tris = boxes.Count * 24;
                var b = Begin(verts, tris, out var vs, out var ist);
                for (int k = 0; k < boxes.Count; k++)
                {
                    foreach (var p in boxes[k]) vs.AddVertex(new VertexPosition(p));
                    int o = k * 8;
                    foreach (var f in Faces)
                    {
                        ist.AddTriangle(new IndexTriangle(o + f[0], o + f[1], o + f[2]));
                        ist.AddTriangle(new IndexTriangle(o + f[0], o + f[2], o + f[3]));
                        ist.AddTriangle(new IndexTriangle(o + f[0], o + f[2], o + f[1]));
                        ist.AddTriangle(new IndexTriangle(o + f[0], o + f[3], o + f[2]));
                    }
                }
                return b.End(verts, tris, color, transparency);
            }

            private static Batch Begin(int verts, int tris, out VertexStreamPosition vs, out IndexStreamTriangle ist)
            {
                var b = new Batch();
                int floats = verts * VertexPosition.GetSizeInFloats();
                b.Vb = new VertexBuffer(floats);
                b.Vb.Map(floats);
                vs = b.Vb.GetVertexStreamPosition();
                int idx = tris * IndexTriangle.GetSizeInShortInts();
                b.Ib = new IndexBuffer(idx);
                b.Ib.Map(idx);
                ist = b.Ib.GetIndexStreamTriangle();
                return b;
            }

            private Batch End(int verts, int tris, Color color, double transparency)
            {
                Vb.Unmap();
                Ib.Unmap();
                Verts = verts;
                Prims = tris;
                Indices = tris * IndexTriangle.GetSizeInShortInts();
                Format = new VertexFormat(VertexFormatBits.Position);
                Effect = new EffectInstance(VertexFormatBits.Position);
                Effect.SetColor(color);
                Effect.SetEmissiveColor(color);
                if (transparency > 0) Effect.SetTransparency(transparency);
                return this;
            }
        }
    }
}
