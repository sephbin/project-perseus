using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    public class DiffOptions
    {
        /// <summary>Length tolerance in feet (default 1 mm).</summary>
        public double Tolerance { get; set; } = 1.0 / 304.8;
        /// <summary>Relative tolerance for solid volume / area (default 0.1 %).</summary>
        public double RelativeTolerance { get; set; } = 0.001;
        public bool CompareParameters { get; set; } = true;
        public bool CompareTypeParameters { get; set; } = true;
        /// <summary>Parameter names never reported (bookkeeping that changes without a design change).</summary>
        public HashSet<string> IgnoredParameters { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Edited by", "Workset", "Phase Created", "Phase Demolished", "Design Option",
        };
    }

    /// <summary>
    /// Pure comparison of two snapshots of the same model (old archived version vs the loaded link), matched by
    /// element UniqueId. No Revit API: unit-tested in ProjectPerseusTests/LinkDiffTest.cs.
    /// </summary>
    public static class LinkDiff
    {
        public const string TypeParameterPrefix = "[Type] ";

        public static List<ElementChange> Compare(LinkSnapshot oldSnap, LinkSnapshot newSnap, DiffOptions options = null)
        {
            options = options ?? new DiffOptions();
            var oldById = Index(oldSnap);
            var newById = Index(newSnap);
            var changes = new List<ElementChange>();

            foreach (var n in newById.Values)
            {
                if (!oldById.TryGetValue(n.UniqueId, out var o))
                {
                    changes.Add(Describe(ChangeKind.Added, n, null, n));
                    continue;
                }
                var c = CompareElement(o, n, options);
                if (c != null) changes.Add(c);
            }
            foreach (var o in oldById.Values)
                if (!newById.ContainsKey(o.UniqueId))
                    changes.Add(Describe(ChangeKind.Deleted, o, o, null));

            return changes
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Id)
                .ToList();
        }

        private static Dictionary<string, ElementSnapshot> Index(LinkSnapshot snap)
        {
            var d = new Dictionary<string, ElementSnapshot>(StringComparer.Ordinal);
            if (snap?.Elements == null) return d;
            foreach (var e in snap.Elements)
                if (!string.IsNullOrEmpty(e?.UniqueId) && !d.ContainsKey(e.UniqueId)) d.Add(e.UniqueId, e);
            return d;
        }

        private static ElementChange Describe(ChangeKind kind, ElementSnapshot e, ElementSnapshot o, ElementSnapshot n) =>
            new ElementChange
            {
                Kind = kind,
                UniqueId = e.UniqueId,
                Id = e.Id,
                Category = e.Category,
                Family = e.Family,
                Type = e.Type,
                Level = e.Level,
                OldMin = o?.Min, OldMax = o?.Max,
                NewMin = n?.Min, NewMax = n?.Max,
            };

        /// <summary>The change between two versions of one element, or null when nothing relevant differs.</summary>
        public static ElementChange CompareElement(ElementSnapshot o, ElementSnapshot n, DiffOptions options = null)
        {
            options = options ?? new DiffOptions();
            var c = Describe(ChangeKind.Modified, n, o, n);
            var a = ChangeAspect.None;

            if (!Same(o.Category, n.Category)) a |= ChangeAspect.Category;
            if (!Same(o.Family, n.Family) || !Same(o.Type, n.Type))
            {
                a |= ChangeAspect.Type;
                c.OldType = string.IsNullOrEmpty(o.Family) ? o.Type : o.Family + " : " + o.Type;
            }
            if (!Same(o.Level, n.Level)) { a |= ChangeAspect.Level; c.OldLevel = o.Level; }
            if (!Same(o.Workset, n.Workset)) a |= ChangeAspect.Workset;

            double moved = Distance(Centre(o), Centre(n));
            if (moved > options.Tolerance) { a |= ChangeAspect.Moved; c.MoveDistance = moved; }
            if (GeometryChanged(o, n, options)) a |= ChangeAspect.Geometry;

            if (options.CompareParameters)
            {
                c.ParameterChanges = ParameterChanges(o.Parameters, n.Parameters, options);
                if (c.ParameterChanges.Count > 0) a |= ChangeAspect.Parameters;
            }

            if (a == ChangeAspect.None) return null;
            c.Aspects = a;
            return c;
        }

        public static List<ParameterChange> ParameterChanges(Dictionary<string, string> o, Dictionary<string, string> n,
                                                             DiffOptions options)
        {
            o = o ?? new Dictionary<string, string>();
            n = n ?? new Dictionary<string, string>();
            var list = new List<ParameterChange>();
            foreach (var name in o.Keys.Union(n.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                bool isType = name.StartsWith(TypeParameterPrefix, StringComparison.Ordinal);
                string bare = isType ? name.Substring(TypeParameterPrefix.Length) : name;
                if (isType && !options.CompareTypeParameters) continue;
                if (options.IgnoredParameters.Contains(bare)) continue;

                o.TryGetValue(name, out var ov);
                n.TryGetValue(name, out var nv);
                bool hadOld = o.ContainsKey(name), hasNew = n.ContainsKey(name);
                // A parameter that's empty in one version and missing in the other isn't a design change.
                if (string.IsNullOrEmpty(ov) && string.IsNullOrEmpty(nv)) continue;
                if (hadOld && hasNew && string.Equals(ov, nv, StringComparison.Ordinal)) continue;
                list.Add(new ParameterChange { Name = name, Old = hadOld ? ov : null, New = hasNew ? nv : null });
            }
            return list;
        }

        private static bool GeometryChanged(ElementSnapshot o, ElementSnapshot n, DiffOptions options)
        {
            if (o.Volume.HasValue != n.Volume.HasValue) return true;
            if (o.Volume.HasValue)
            {
                if (!Close(o.Volume.Value, n.Volume.Value, options.RelativeTolerance, options.Tolerance * options.Tolerance * options.Tolerance)) return true;
                if (!Close(o.Area ?? 0, n.Area ?? 0, options.RelativeTolerance, options.Tolerance * options.Tolerance)) return true;
                if (o.FaceCount != n.FaceCount || o.EdgeCount != n.EdgeCount) return true;
            }
            // Bounding box size (not position: moving is reported as Moved).
            if ((o.Min == null) != (n.Min == null)) return true;
            if (o.Min != null && o.Max != null && n.Max != null)
                for (int i = 0; i < 3; i++)
                    if (Math.Abs((o.Max[i] - o.Min[i]) - (n.Max[i] - n.Min[i])) > options.Tolerance) return true;
            // Curve-driven elements (walls, beams, pipes): a changed length or direction is a shape change.
            if (o.Location?.Length == 6 && n.Location?.Length == 6)
            {
                double ol = Distance(Pt(o.Location, 0), Pt(o.Location, 3));
                double nl = Distance(Pt(n.Location, 0), Pt(n.Location, 3));
                if (Math.Abs(ol - nl) > options.Tolerance) return true;
            }
            return false;
        }

        private static bool Close(double a, double b, double rel, double abs) =>
            Math.Abs(a - b) <= Math.Max(abs, rel * Math.Max(Math.Abs(a), Math.Abs(b)));

        /// <summary>Location point, location curve midpoint, else bounding box centre; null when none.</summary>
        public static double[] Centre(ElementSnapshot e)
        {
            if (e.Location?.Length == 3) return e.Location;
            if (e.Location?.Length == 6)
                return new[] { (e.Location[0] + e.Location[3]) / 2, (e.Location[1] + e.Location[4]) / 2, (e.Location[2] + e.Location[5]) / 2 };
            if (e.Min?.Length == 3 && e.Max?.Length == 3)
                return new[] { (e.Min[0] + e.Max[0]) / 2, (e.Min[1] + e.Max[1]) / 2, (e.Min[2] + e.Max[2]) / 2 };
            return null;
        }

        private static double[] Pt(double[] a, int offset) => new[] { a[offset], a[offset + 1], a[offset + 2] };

        private static double Distance(double[] a, double[] b)
        {
            if (a == null || b == null) return 0;
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }
}
