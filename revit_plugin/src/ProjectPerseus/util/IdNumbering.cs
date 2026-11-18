using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectPerseus.util
{
    /// <summary>
    /// How an ID rule picks numbers (IdRuleDto.IncrementMode). Pure, no Revit dependencies; tested in IdPatternTest.cs.
    ///   keep - any valid, unique value is kept; new numbers continue after the highest ever seen (original behaviour).
    ///   next - new numbers continue after the highest number currently held (no gap filling).
    ///   fill - new numbers take the lowest free number (gap filling).
    /// With next / fill, a value the element arrived with (a new or copied element: Revit's own next Mark, a pasted
    /// value) or kept after moving to another "unique with" group (another room) is not trusted: it is minimised to the
    /// number the mode would pick, so the only door in a room becomes D01, not D99.
    /// </summary>
    public static class IdNumbering
    {
        public const string Keep = "keep";
        public const string Next = "next";
        public const string Fill = "fill";

        public static bool Minimises(string mode) => mode == Next || mode == Fill;

        /// <summary>
        /// The number <paramref name="mode"/> picks given the numbers other elements hold in the counter group.
        /// <paramref name="taken"/> = numbers known to be held elsewhere (another user's claims). Not for "keep".
        /// </summary>
        public static long Pick(string mode, IEnumerable<long> used, Func<long, bool> taken = null)
        {
            var held = new HashSet<long>(used);
            long n;
            if (mode == Fill)
            {
                n = 1;
                while (held.Contains(n) || (taken != null && taken(n))) n++;
                return n;
            }
            n = (held.Count == 0 ? 0 : held.Max()) + 1;
            while (taken != null && taken(n)) n++;
            return n;
        }

        public static string Normalise(string mode)
        {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            return mode == Next || mode == Fill ? mode : Keep;
        }
    }
}
