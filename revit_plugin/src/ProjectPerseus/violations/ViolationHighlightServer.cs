using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using ProjectPerseus.logging;

namespace ProjectPerseus.violations
{
    public enum ViolationDisplayMode { BoundingBox, Symbol }

    // Shared data struct — internal so ViolationHighlightController can build the list.
    internal struct ViolationHighlight
    {
        internal BoundingBoxXYZ BBox;
        internal XYZ Location;
        internal Color Color;
    }

    internal class ViolationHighlightServer : IDirectContext3DServer
    {
        internal static readonly ViolationHighlightServer Instance = new ViolationHighlightServer();

        // Hardcoded GUID — must never change once deployed; identifies this server across sessions.
        private static readonly Guid _serverId = new Guid("A8B2C3D4-E5F6-7890-ABCD-EF1234567890");

        // 100 mm expressed in Revit decimal feet (= 1 / 304.8 × 100).
        private const double _SYMBOL_FT = 100.0 / 304.8;

        private readonly object _lock = new object();
        private List<ViolationHighlight> _highlights = new List<ViolationHighlight>();

        internal volatile bool IsEnabled = false;
        internal ViolationDisplayMode CurrentMode = ViolationDisplayMode.BoundingBox;

        private ViolationHighlightServer() { }

        internal void SetHighlights(List<ViolationHighlight> items)
        {
            lock (_lock) { _highlights = items ?? new List<ViolationHighlight>(); }
        }

        // ── IExternalServer ──────────────────────────────────────────────────────
        public Guid GetServerId() => _serverId;
        public ExternalServiceId GetServiceId() =>
            ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public string GetName()          => "Perseus Violation Highlight";
        public string GetVendorId()      => "Perseus";
        public string GetDescription()   => "Draws coloured wireframes / symbols around elements with rule violations.";
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

            var mode = CurrentMode;
            foreach (var h in snapshot)
            {
                try
                {
                    if (mode == ViolationDisplayMode.BoundingBox && h.BBox != null)
                        DrawBoundingBox(h.BBox, h.Color);
                    else if (mode == ViolationDisplayMode.Symbol && h.Location != null)
                        DrawSymbol(h.Location, h.Color);
                    else if (h.BBox != null)
                        DrawBoundingBox(h.BBox, h.Color);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ViolationHighlightServer] draw failed: {ex.Message}");
                }
            }
        }

        private static void DrawBoundingBox(BoundingBoxXYZ bbox, Color color)
        {
            var mn = bbox.Min;
            var mx = bbox.Max;

            int nVerts = 8;
            var vb = new VertexBuffer(nVerts * VertexPosition.GetSizeInFloats());
            vb.Map(nVerts * VertexPosition.GetSizeInFloats());
            var vs = vb.GetVertexStreamPosition();
            vs.AddVertex(new VertexPosition(new XYZ(mn.X, mn.Y, mn.Z))); // 0
            vs.AddVertex(new VertexPosition(new XYZ(mx.X, mn.Y, mn.Z))); // 1
            vs.AddVertex(new VertexPosition(new XYZ(mx.X, mx.Y, mn.Z))); // 2
            vs.AddVertex(new VertexPosition(new XYZ(mn.X, mx.Y, mn.Z))); // 3
            vs.AddVertex(new VertexPosition(new XYZ(mn.X, mn.Y, mx.Z))); // 4
            vs.AddVertex(new VertexPosition(new XYZ(mx.X, mn.Y, mx.Z))); // 5
            vs.AddVertex(new VertexPosition(new XYZ(mx.X, mx.Y, mx.Z))); // 6
            vs.AddVertex(new VertexPosition(new XYZ(mn.X, mx.Y, mx.Z))); // 7
            vb.Unmap();

            int nIndices = 24; // 12 edges × 2 indices each
            var ib = new IndexBuffer(nIndices);
            ib.Map(nIndices);
            var ist = ib.GetIndexStreamLine();
            // bottom face
            ist.AddLine(new IndexLine(0, 1)); ist.AddLine(new IndexLine(1, 2));
            ist.AddLine(new IndexLine(2, 3)); ist.AddLine(new IndexLine(3, 0));
            // top face
            ist.AddLine(new IndexLine(4, 5)); ist.AddLine(new IndexLine(5, 6));
            ist.AddLine(new IndexLine(6, 7)); ist.AddLine(new IndexLine(7, 4));
            // vertical edges
            ist.AddLine(new IndexLine(0, 4)); ist.AddLine(new IndexLine(1, 5));
            ist.AddLine(new IndexLine(2, 6)); ist.AddLine(new IndexLine(3, 7));
            ib.Unmap();

            var fmt    = new VertexFormat(VertexFormatBits.Position);
            var effect = new EffectInstance(VertexFormatBits.Position);
            effect.SetColor(color);
            DrawContext.FlushBuffer(vb, nVerts, ib, nIndices, fmt, effect, PrimitiveType.LineList, 0, 12);
        }

        private static void DrawSymbol(XYZ center, Color color)
        {
            double arm = _SYMBOL_FT;
            int nVerts = 6;
            var vb = new VertexBuffer(nVerts * VertexPosition.GetSizeInFloats());
            vb.Map(nVerts * VertexPosition.GetSizeInFloats());
            var vs = vb.GetVertexStreamPosition();
            vs.AddVertex(new VertexPosition(new XYZ(center.X - arm, center.Y,        center.Z)));       // 0
            vs.AddVertex(new VertexPosition(new XYZ(center.X + arm, center.Y,        center.Z)));       // 1
            vs.AddVertex(new VertexPosition(new XYZ(center.X,        center.Y - arm, center.Z)));       // 2
            vs.AddVertex(new VertexPosition(new XYZ(center.X,        center.Y + arm, center.Z)));       // 3
            vs.AddVertex(new VertexPosition(new XYZ(center.X,        center.Y,        center.Z - arm))); // 4
            vs.AddVertex(new VertexPosition(new XYZ(center.X,        center.Y,        center.Z + arm))); // 5
            vb.Unmap();

            int nIndices = 6; // 3 lines × 2 indices each
            var ib = new IndexBuffer(nIndices);
            ib.Map(nIndices);
            var ist = ib.GetIndexStreamLine();
            ist.AddLine(new IndexLine(0, 1)); // X axis
            ist.AddLine(new IndexLine(2, 3)); // Y axis
            ist.AddLine(new IndexLine(4, 5)); // Z axis
            ib.Unmap();

            var fmt    = new VertexFormat(VertexFormatBits.Position);
            var effect = new EffectInstance(VertexFormatBits.Position);
            effect.SetColor(color);
            DrawContext.FlushBuffer(vb, nVerts, ib, nIndices, fmt, effect, PrimitiveType.LineList, 0, 3);
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
