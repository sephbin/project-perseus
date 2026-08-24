using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using ProjectPerseus.logging;

namespace ProjectPerseus.violations
{
    public enum ViolationDisplayMode { BoundingBox, Symbol }

    internal struct ViolationHighlight
    {
        internal BoundingBoxXYZ BBox;
        internal XYZ Location;
        internal Color Color;
    }

    internal class ViolationHighlightServer : IDirectContext3DServer
    {
        internal static readonly ViolationHighlightServer Instance = new ViolationHighlightServer();

        private static readonly Guid _serverId = new Guid("A8B2C3D4-E5F6-7890-ABCD-EF1234567890");

        // Half-width of each bounding-box edge quad in Revit feet (~75 mm).
        // Increase to make boxes thicker; decrease to thin them.
        private const double _EDGE_HALF_FT = 0.25;

        private readonly object _lock = new object();
        private List<ViolationHighlight> _highlights = new List<ViolationHighlight>();

        internal volatile bool IsEnabled = false;
        internal ViolationDisplayMode CurrentMode = ViolationDisplayMode.BoundingBox;

        private ViolationHighlightServer() { }

        internal void SetHighlights(List<ViolationHighlight> items)
        {
            lock (_lock) { _highlights = items ?? new List<ViolationHighlight>(); }
        }

        internal List<ViolationHighlight> GetHighlightsSnapshot()
        {
            lock (_lock) { return new List<ViolationHighlight>(_highlights); }
        }

        // ── IExternalServer ──────────────────────────────────────────────────────
        public Guid GetServerId() => _serverId;
        public ExternalServiceId GetServiceId() =>
            ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName()          => "Perseus Violation Highlight";
        public string GetVendorId()      => "Perseus";
        public string GetDescription()   => "Draws coloured wireframe boxes around elements with rule violations.";
        public string GetApplicationId() => _serverId.ToString();
        public string GetSourceId()      => string.Empty;
        public bool UsesHandles()        => false;

        // ── IDirectContext3DServer ───────────────────────────────────────────────
        public bool CanExecute(View view)
        {
            if (!IsEnabled || view == null) return false;
            var vt = view.ViewType;
            return vt == ViewType.ThreeD
                || vt == ViewType.FloorPlan
                || vt == ViewType.CeilingPlan
                || vt == ViewType.Section
                || vt == ViewType.Elevation
                || vt == ViewType.Detail;
        }

        public Outline GetBoundingBox(View view) => null;

        public bool UseInTransparentPass(View view) => false;

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            List<ViolationHighlight> snapshot;
            lock (_lock) { snapshot = new List<ViolationHighlight>(_highlights); }

            // Symbol mode is handled by the WPF screen-space overlay.
            if (CurrentMode != ViolationDisplayMode.BoundingBox) return;

            XYZ viewDir = view.ViewDirection;

            foreach (var h in snapshot)
            {
                if (h.BBox == null) continue;
                try   { DrawBoundingBox(h.BBox, h.Color, viewDir); }
                catch (Exception ex) { Log.Warn($"[ViolationHighlightServer] draw failed: {ex.Message}"); }
            }
        }

        // Each of the 12 bounding-box edges is rendered as a view-facing quad (pair of
        // triangles) rather than a line primitive, because DirectContext3D line primitives
        // are fixed at 1 px regardless of any API calls.  Both faces of each quad are
        // emitted so the boxes are visible from any viewing angle.
        private static void DrawBoundingBox(BoundingBoxXYZ bbox, Color color, XYZ viewDir)
        {
            var mn = bbox.Min;
            var mx = bbox.Max;

            XYZ[] c =
            {
                new XYZ(mn.X, mn.Y, mn.Z), // 0
                new XYZ(mx.X, mn.Y, mn.Z), // 1
                new XYZ(mx.X, mx.Y, mn.Z), // 2
                new XYZ(mn.X, mx.Y, mn.Z), // 3
                new XYZ(mn.X, mn.Y, mx.Z), // 4
                new XYZ(mx.X, mn.Y, mx.Z), // 5
                new XYZ(mx.X, mx.Y, mx.Z), // 6
                new XYZ(mn.X, mx.Y, mx.Z), // 7
            };

            // 12 edges as (start, end) corner-index pairs.
            int[] s = { 0, 1, 2, 3,  4, 5, 6, 7,  0, 1, 2, 3 };
            int[] e = { 1, 2, 3, 0,  5, 6, 7, 4,  4, 5, 6, 7 };

            const int nEdges = 12;
            const int nVerts = nEdges * 4;       // 4 verts per quad
            const int nTris  = nEdges * 4;       // 2 front + 2 back triangles per quad
            const int nIdx   = nTris  * 3;

            var vb = new VertexBuffer(nVerts * VertexPosition.GetSizeInFloats());
            vb.Map(nVerts * VertexPosition.GetSizeInFloats());
            var vs = vb.GetVertexStreamPosition();

            var ib = new IndexBuffer(nIdx);
            ib.Map(nIdx);
            var ist = ib.GetIndexStreamTriangle();

            for (int i = 0; i < nEdges; i++)
            {
                XYZ a = c[s[i]];
                XYZ b = c[e[i]];

                XYZ edgeVec = b.Subtract(a);
                double len  = edgeVec.GetLength();
                if (len < 1e-9) continue;
                XYZ edgeDir = edgeVec.Multiply(1.0 / len);

                // Perpendicular in the plane of (edgeDir, viewDir), scaled to half-width.
                XYZ perp = edgeDir.CrossProduct(viewDir);
                if (perp.GetLength() < 1e-6)
                {
                    // Edge nearly parallel to view — use any perpendicular to edgeDir.
                    XYZ fallback = Math.Abs(edgeDir.X) < 0.9 ? new XYZ(1, 0, 0) : new XYZ(0, 1, 0);
                    perp = edgeDir.CrossProduct(fallback);
                }
                double pLen = perp.GetLength();
                if (pLen < 1e-9) continue;
                perp = perp.Multiply(_EDGE_HALF_FT / pLen);

                // Quad vertices:  v0 = a+perp,  v1 = a-perp,  v2 = b+perp,  v3 = b-perp
                vs.AddVertex(new VertexPosition(a.Add(perp)));      // 4i+0
                vs.AddVertex(new VertexPosition(a.Subtract(perp))); // 4i+1
                vs.AddVertex(new VertexPosition(b.Add(perp)));      // 4i+2
                vs.AddVertex(new VertexPosition(b.Subtract(perp))); // 4i+3

                int v = i * 4;
                // Front face (normal toward viewer)
                ist.AddTriangle(new IndexTriangle(v,   v+2, v+1));
                ist.AddTriangle(new IndexTriangle(v+1, v+2, v+3));
                // Back face (normal away from viewer — ensures visibility from all angles)
                ist.AddTriangle(new IndexTriangle(v,   v+1, v+2));
                ist.AddTriangle(new IndexTriangle(v+1, v+3, v+2));
            }

            vb.Unmap();
            ib.Unmap();

            var fmt    = new VertexFormat(VertexFormatBits.Position);
            var effect = new EffectInstance(VertexFormatBits.Position);
            effect.SetColor(color);
            DrawContext.FlushBuffer(vb, nVerts, ib, nIdx, fmt, effect, PrimitiveType.TriangleList, 0, nTris);
        }

        internal static Color SeverityColor(string severity)
        {
            switch (severity?.ToLower())
            {
                case "error":   return new Color(220, 60,  60);
                case "warning": return new Color(230, 150, 30);
                default:        return new Color(80,  160, 220);
            }
        }
    }
}
