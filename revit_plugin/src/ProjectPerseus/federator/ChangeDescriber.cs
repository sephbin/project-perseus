using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Plain-language description of an ElementChange for the review panel: a short Summary for the grid and
    /// detailed Lines for the detail pane ("Moved 120 mm horizontally and 15 mm up", "Length 4.2 m → 4.8 m").
    /// Lengths are metric (mm / m). Pure: unit-tested in ProjectPerseusTests/ChangeDescriberTest.cs.
    /// </summary>
    public static class ChangeDescriber
    {
        private const double FtToMm = 304.8;
        private const double Tol = 1 / 304.8;
        private const int MaxNamedParameters = 3;

        public static string Length(double feet)
        {
            double mm = feet * FtToMm;
            return Math.Abs(mm) >= 1000
                ? (mm / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " m"
                : Math.Round(mm).ToString("0", CultureInfo.InvariantCulture) + " mm";
        }

        /// <summary>A purely vertical move (no horizontal component); needs MoveVector (older comparisons: false).</summary>
        public static bool IsVerticalMove(ElementChange c)
        {
            var v = c?.MoveVector;
            if (v == null || v.Length != 3) return false;
            return Math.Sqrt(v[0] * v[0] + v[1] * v[1]) <= Tol && Math.Abs(v[2]) > Tol;
        }

        /// <summary>"Moved" (3D or horizontal) or "Moved vertically" (Z only).</summary>
        public static string MoveLabel(ElementChange c) => IsVerticalMove(c) ? "Moved vertically" : "Moved";

        public static string TypeName(string family, string type) =>
            string.IsNullOrEmpty(family) ? (type ?? "") : family + " : " + type;

        /// <summary>One line for the grid's Change column.</summary>
        public static string Summary(ElementChange c)
        {
            switch (c.Kind)
            {
                case ChangeKind.Added: return "Added";
                case ChangeKind.Deleted:
                    return c.Replacements != null && c.Replacements.Count > 0 ? "Deleted · replaced?" : "Deleted";
            }
            var parts = new List<string>();
            if (c.Aspects.HasFlag(ChangeAspect.Moved)) parts.Add(MoveLabel(c) + " " + Length(c.MoveDistance));
            if (c.Aspects.HasFlag(ChangeAspect.Geometry)) parts.Add(GeometryShort(c));
            if (c.Aspects.HasFlag(ChangeAspect.Type)) parts.Add("Type");
            if (c.Aspects.HasFlag(ChangeAspect.Level)) parts.Add("Level");
            if (c.Aspects.HasFlag(ChangeAspect.Category)) parts.Add("Category");
            if (c.Aspects.HasFlag(ChangeAspect.Workset)) parts.Add("Workset");
            if (c.Aspects.HasFlag(ChangeAspect.Parameters))
            {
                int n = c.ParameterChanges?.Count ?? 0;
                parts.Add(n == 1 ? c.ParameterChanges[0].Name : n + " params");
            }
            return parts.Count == 0 ? "Modified" : string.Join(" · ", parts);
        }

        /// <summary>Sentences for the detail pane.</summary>
        public static List<string> Lines(ElementChange c)
        {
            var lines = new List<string>();
            switch (c.Kind)
            {
                case ChangeKind.Added:
                    lines.Add($"Added: {TypeName(c.Family, c.Type)}" + (string.IsNullOrEmpty(c.Level) ? "" : $" on {c.Level}") + ".");
                    if (c.NewLength.HasValue) lines.Add($"Length {Length(c.NewLength.Value)}.");
                    return lines;
                case ChangeKind.Deleted:
                    lines.Add($"Deleted: was {TypeName(c.Family, c.Type)}" + (string.IsNullOrEmpty(c.Level) ? "" : $" on {c.Level}") + ".");
                    if (c.Replacements == null || c.Replacements.Count == 0) lines.Add("Nothing similar was added nearby.");
                    return lines;
            }

            if (c.Aspects.HasFlag(ChangeAspect.Moved)) lines.Add(Movement(c));
            if (c.Aspects.HasFlag(ChangeAspect.Geometry)) lines.AddRange(Geometry(c));
            if (c.Aspects.HasFlag(ChangeAspect.Type))
                lines.Add($"Type changed: {c.OldType} → {TypeName(c.Family, c.Type)}.");
            if (c.Aspects.HasFlag(ChangeAspect.Level))
                lines.Add($"Level changed: {Or(c.OldLevel, "none")} → {Or(c.Level, "none")}.");
            if (c.Aspects.HasFlag(ChangeAspect.Category)) lines.Add("Category changed.");
            if (c.Aspects.HasFlag(ChangeAspect.Workset)) lines.Add("Moved to another workset.");
            if (c.Aspects.HasFlag(ChangeAspect.Parameters) && c.ParameterChanges != null && c.ParameterChanges.Count > 0)
            {
                var names = c.ParameterChanges.Take(MaxNamedParameters).Select(p => p.Name).ToList();
                int more = c.ParameterChanges.Count - names.Count;
                lines.Add((c.ParameterChanges.Count == 1 ? "1 parameter changed: " : $"{c.ParameterChanges.Count} parameters changed: ")
                          + string.Join(", ", names) + (more > 0 ? $" and {more} more" : "") + ".");
            }
            if (lines.Count == 0) lines.Add("Modified.");
            return lines;
        }

        private static string Movement(ElementChange c)
        {
            var v = c.MoveVector;
            if (v == null || v.Length != 3) return $"Moved {Length(c.MoveDistance)}.";
            double h = Math.Sqrt(v[0] * v[0] + v[1] * v[1]);
            double z = v[2];
            bool horiz = h > Tol, vert = Math.Abs(z) > Tol;
            string up = z > 0 ? "up" : "down";
            if (horiz && vert) return $"Moved {Length(c.MoveDistance)} ({Length(h)} horizontally, {Length(Math.Abs(z))} {up}).";
            if (vert) return $"Moved vertically {Length(Math.Abs(z))} {up}.";
            return $"Moved {Length(h)} horizontally.";
        }

        private static string GeometryShort(ElementChange c)
        {
            if (c.OldLength.HasValue && c.NewLength.HasValue && Math.Abs(c.NewLength.Value - c.OldLength.Value) > Tol)
                return "Length " + Signed(c.NewLength.Value - c.OldLength.Value);
            return "Shape";
        }

        private static IEnumerable<string> Geometry(ElementChange c)
        {
            bool any = false;
            if (c.OldLength.HasValue && c.NewLength.HasValue && Math.Abs(c.NewLength.Value - c.OldLength.Value) > Tol)
            {
                any = true;
                yield return $"Length {Length(c.OldLength.Value)} → {Length(c.NewLength.Value)} ({Signed(c.NewLength.Value - c.OldLength.Value)}).";
            }
            if (c.OldVolume.HasValue && c.NewVolume.HasValue && c.OldVolume.Value > 1e-9)
            {
                double pct = (c.NewVolume.Value - c.OldVolume.Value) / c.OldVolume.Value * 100;
                if (Math.Abs(pct) >= 0.5)
                {
                    any = true;
                    yield return $"Volume {(pct > 0 ? "+" : "−")}{Math.Abs(pct).ToString("0", CultureInfo.InvariantCulture)} %.";
                }
            }
            if (!any) yield return "Shape changed (faces, edges or size).";
        }

        private static string Signed(double feet) => (feet >= 0 ? "+" : "−") + Length(Math.Abs(feet));

        private static string Or(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;
    }
}
