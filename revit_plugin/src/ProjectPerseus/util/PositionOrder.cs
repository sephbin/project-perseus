using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ProjectPerseus.util
{
    /// <summary>Where an element is, in millimetres in project coordinates, plus its ElementId (final tie-break).</summary>
    public struct PositionSample
    {
        public double X, Y, Z;
        public long Id;
    }

    /// <summary>
    /// A numbering order such as "X, -Y" (left to right, then top to bottom) or "-Y~500, X" (rows from the top,
    /// each row left to right). Keys are X, Y, Z or Id; a leading '-' sorts descending; '~N' rounds the value
    /// down to a multiple of N mm so near-equal values count as a tie. ElementId always breaks remaining ties,
    /// so the order is deterministic. Pure logic (no Revit types) so it can be unit-tested.
    /// </summary>
    public sealed class PositionOrder : IComparer<PositionSample>
    {
        private static readonly Regex KeyRe = new Regex(@"^(-?)(X|Y|Z|Id)(?:~([1-9][0-9]{0,5}))?$", RegexOptions.Compiled);

        private struct Key
        {
            public char Axis;        // 'X', 'Y', 'Z' or 'I' (ElementId)
            public bool Descending;
            public double Bucket;    // mm, 0 = exact
        }

        private readonly List<Key> _keys = new List<Key>();

        /// <summary>True when the order is just ElementId order (blank spec).</summary>
        public bool IsDefault => _keys.Count == 0;

        /// <summary>Parses a spec; blank gives the default ElementId order, an invalid spec returns null.</summary>
        public static PositionOrder Parse(string spec)
        {
            var order = new PositionOrder();
            if (string.IsNullOrWhiteSpace(spec)) return order;

            var parts = spec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 4) return null;
            foreach (var raw in parts)
            {
                var m = KeyRe.Match(raw.Trim());
                if (!m.Success) return null;
                order._keys.Add(new Key
                {
                    Axis = m.Groups[2].Value == "Id" ? 'I' : m.Groups[2].Value[0],
                    Descending = m.Groups[1].Value == "-",
                    Bucket = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0,
                });
            }
            return order;
        }

        public int Compare(PositionSample a, PositionSample b)
        {
            foreach (var k in _keys)
            {
                int c = Value(a, k).CompareTo(Value(b, k));
                if (c != 0) return k.Descending ? -c : c;
            }
            return a.Id.CompareTo(b.Id);
        }

        private static double Value(PositionSample s, Key k)
        {
            double v = k.Axis == 'X' ? s.X : k.Axis == 'Y' ? s.Y : k.Axis == 'Z' ? s.Z : s.Id;
            return k.Bucket > 0 ? Math.Floor(v / k.Bucket) : v;
        }
    }
}
