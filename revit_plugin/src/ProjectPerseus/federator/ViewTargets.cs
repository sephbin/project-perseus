using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Which views receive baked annotations: (regex matches ∪ manually included) − manually excluded. Ticking a view
    /// the regex doesn't match includes it; unticking a view the regex matches excludes it - so new views that match
    /// later are picked up automatically. Pure: unit-tested in ProjectPerseusTests/ViewTargetsTest.cs.
    /// </summary>
    public static class ViewTargets
    {
        public enum Reason { None, Regex, Manual, Excluded }

        /// <summary>Case-insensitive regex, or null for an empty pattern; error set (and null returned) when invalid.</summary>
        public static Regex Build(string pattern, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(pattern)) return null;
            try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException ex) { error = ex.Message; return null; }
        }

        public static bool RegexMatches(ViewCandidate v, BakeSettings s, Regex rx)
        {
            if (rx == null || v == null) return false;
            string text = s.RegexTarget == BakeSettings.MatchSheetNumber ? v.SheetNumber : v.Name;
            try { return !string.IsNullOrEmpty(text) && rx.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        public static Reason ReasonFor(ViewCandidate v, BakeSettings s, Regex rx)
        {
            if (s.ExcludedViewUids.Contains(v.Uid)) return Reason.Excluded;
            if (s.IncludedViewUids.Contains(v.Uid)) return Reason.Manual;
            return RegexMatches(v, s, rx) ? Reason.Regex : Reason.None;
        }

        public static bool IsTarget(ViewCandidate v, BakeSettings s, Regex rx)
        {
            var r = ReasonFor(v, s, rx);
            return r == Reason.Regex || r == Reason.Manual;
        }

        public static void Toggle(ViewCandidate v, BakeSettings s, Regex rx, bool on)
        {
            bool matches = RegexMatches(v, s, rx);
            s.IncludedViewUids.Remove(v.Uid);
            s.ExcludedViewUids.Remove(v.Uid);
            if (on && !matches) s.IncludedViewUids.Add(v.Uid);
            if (!on && matches) s.ExcludedViewUids.Add(v.Uid);
        }

        public static List<ViewCandidate> Targets(IEnumerable<ViewCandidate> views, BakeSettings s, Regex rx) =>
            views.Where(v => IsTarget(v, s, rx)).ToList();

        /// <summary>Drops manual entries for views no longer in the model.</summary>
        public static void Prune(BakeSettings s, IEnumerable<ViewCandidate> views)
        {
            var uids = new HashSet<string>(views.Select(v => v.Uid));
            s.IncludedViewUids.RemoveAll(u => !uids.Contains(u));
            s.ExcludedViewUids.RemoveAll(u => !uids.Contains(u));
        }
    }
}
