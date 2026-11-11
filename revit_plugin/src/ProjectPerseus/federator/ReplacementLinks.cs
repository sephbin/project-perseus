using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Orphan fix: confirmed "this deleted element was replaced by that new one" links. Consultants often delete an
    /// element and model a new one (new UniqueId) instead of editing it; a link turns the Deleted + Added pair into one
    /// Replaced change, and Map() gives old UniqueId -> current UniqueId so references to consultant elements (later:
    /// our families mapped to them) can be moved to the new element.
    ///
    /// Links are review events (ReviewEvent.ReplacementOp on the OLD element: "link" + ReplacedByUid, or "reject" =
    /// not a replacement / link broken), append-only like every review, and facts about the consultant model rather
    /// than one comparison: they apply in every comparison of that link instance. Newest event per old element wins;
    /// links are one-to-one (a newer link to the same new element replaces the older one). Pure (no Revit API):
    /// unit-tested in ProjectPerseusTests/ReplacementLinksTest.cs.
    /// </summary>
    public static class ReplacementLinks
    {
        public const string OpLink = "link";
        public const string OpReject = "reject";

        public class Link
        {
            public string OldUid { get; set; }
            public long OldId { get; set; }
            public string NewUid { get; set; }
            public long NewId { get; set; }
            public string Author { get; set; }
            public DateTime Utc { get; set; }
        }

        public class State
        {
            /// <summary>Old (deleted) UniqueId -> link.</summary>
            public Dictionary<string, Link> ByOld { get; } = new Dictionary<string, Link>(StringComparer.Ordinal);
            /// <summary>New UniqueId -> link.</summary>
            public Dictionary<string, Link> ByNew { get; } = new Dictionary<string, Link>(StringComparer.Ordinal);
            /// <summary>Old UniqueIds marked "not a replacement" (or whose link was broken): no longer suggested.</summary>
            public HashSet<string> Rejected { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        public static State Fold(IEnumerable<ReviewEvent> events, string linkInstanceUid)
        {
            var s = new State();
            if (events == null) return s;
            foreach (var e in events.Where(x => x != null && !string.IsNullOrEmpty(x.ReplacementOp)
                                                && string.Equals(x.LinkInstanceUid, linkInstanceUid, StringComparison.Ordinal))
                                    .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                string old = e.LinkedElementUid;
                if (string.IsNullOrEmpty(old)) continue;
                Unlink(s, old);
                if (e.ReplacementOp == OpLink && !string.IsNullOrEmpty(e.ReplacedByUid) && e.ReplacedByUid != old)
                {
                    if (s.ByNew.TryGetValue(e.ReplacedByUid, out var previous)) Unlink(s, previous.OldUid);
                    var link = new Link { OldUid = old, OldId = e.LinkedElementId, NewUid = e.ReplacedByUid, NewId = e.ReplacedById,
                                          Author = e.Author, Utc = e.CreatedUtc };
                    s.ByOld[old] = link;
                    s.ByNew[link.NewUid] = link;
                    s.Rejected.Remove(old);
                }
                else if (e.ReplacementOp == OpReject) s.Rejected.Add(old);
            }
            return s;
        }

        private static void Unlink(State s, string old)
        {
            if (!s.ByOld.TryGetValue(old, out var l)) return;
            s.ByOld.Remove(old);
            if (s.ByNew.TryGetValue(l.NewUid, out var n) && n == l) s.ByNew.Remove(l.NewUid);
        }

        /// <summary>
        /// Old UniqueId -> the element that replaces it now, following chains (A replaced by B, later B by C: A -> C).
        /// For moving references to consultant elements onto their replacements.
        /// </summary>
        public static Dictionary<string, string> Map(State s)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var old in s.ByOld.Keys)
            {
                string cur = old;
                var seen = new HashSet<string>(StringComparer.Ordinal) { cur };
                while (s.ByOld.TryGetValue(cur, out var l) && seen.Add(l.NewUid)) cur = l.NewUid;
                map[old] = cur;
            }
            return map;
        }

        /// <summary>
        /// The changes as the panel shows them: each linked Deleted + Added pair becomes one Replaced change (identity of
        /// the new element, old box / type / level from the deleted one). A link whose new element isn't added in this
        /// comparison still turns the deleted change into Replaced (new boxes unknown). Stored changes aren't touched.
        /// </summary>
        public static List<ElementChange> Apply(IList<ElementChange> changes, State s, double tolerance = 1 / 304.8)
        {
            var result = new List<ElementChange>();
            if (changes == null) return result;
            if (s == null || s.ByOld.Count == 0) return changes.ToList();
            var added = changes.Where(c => c.Kind == ChangeKind.Added && c.UniqueId != null)
                               .GroupBy(c => c.UniqueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in changes)
            {
                if (c.Kind == ChangeKind.Deleted && c.UniqueId != null && s.ByOld.TryGetValue(c.UniqueId, out var link))
                {
                    added.TryGetValue(link.NewUid, out var a);
                    if (a != null) used.Add(a.UniqueId);
                    result.Add(Merge(c, a, link, tolerance));
                }
            }
            // Keep the original order; merged rows take the deleted element's place.
            var merged = result.ToDictionary(r => r.OldUniqueId, StringComparer.Ordinal);
            var ordered = new List<ElementChange>(changes.Count);
            foreach (var c in changes)
            {
                if (c.Kind == ChangeKind.Added && c.UniqueId != null && used.Contains(c.UniqueId)) continue;
                if (c.Kind == ChangeKind.Deleted && c.UniqueId != null && merged.TryGetValue(c.UniqueId, out var m)) { ordered.Add(m); continue; }
                ordered.Add(c);
            }
            return ordered;
        }

        /// <summary>One Replaced change from a deleted element and the added one that replaces it (null: not in this comparison).</summary>
        public static ElementChange Merge(ElementChange deleted, ElementChange added, Link link, double tolerance = 1 / 304.8)
        {
            var r = new ElementChange
            {
                Kind = ChangeKind.Replaced,
                UniqueId = added?.UniqueId ?? link?.NewUid,
                Id = added?.Id ?? link?.NewId ?? 0,
                OldUniqueId = deleted.UniqueId,
                OldId = deleted.Id,
                Category = added?.Category ?? deleted.Category,
                Family = added?.Family ?? deleted.Family,
                Type = added?.Type ?? deleted.Type,
                Level = added?.Level ?? deleted.Level,
                OldMin = deleted.OldMin, OldMax = deleted.OldMax, OldObb = deleted.OldObb,
                NewMin = added?.NewMin, NewMax = added?.NewMax, NewObb = added?.NewObb,
                OldLength = deleted.OldLength, NewLength = added?.NewLength,
                OldVolume = deleted.OldVolume, NewVolume = added?.NewVolume,
                Signature = "replaced:" + deleted.Signature + ">" + (added?.Signature ?? link?.NewUid),
            };
            if (added == null) return r;

            var aspects = ChangeAspect.None;
            string oldType = ChangeDescriber.TypeName(deleted.Family, deleted.Type), newType = ChangeDescriber.TypeName(added.Family, added.Type);
            if (!string.Equals(oldType, newType, StringComparison.Ordinal)) { aspects |= ChangeAspect.Type; r.OldType = oldType; }
            if (!string.Equals(deleted.Level ?? "", added.Level ?? "", StringComparison.Ordinal)) { aspects |= ChangeAspect.Level; r.OldLevel = deleted.Level; }
            if (!string.Equals(deleted.Category ?? "", added.Category ?? "", StringComparison.Ordinal)) aspects |= ChangeAspect.Category;
            if (deleted.OldMin != null && deleted.OldMax != null && added.NewMin != null && added.NewMax != null)
            {
                var v = new double[3];
                bool sizeChanged = false;
                for (int i = 0; i < 3; i++)
                {
                    v[i] = (added.NewMin[i] + added.NewMax[i]) / 2 - (deleted.OldMin[i] + deleted.OldMax[i]) / 2;
                    double so = deleted.OldMax[i] - deleted.OldMin[i], sn = added.NewMax[i] - added.NewMin[i];
                    if (Math.Abs(sn - so) > tolerance) sizeChanged = true;
                }
                double d = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                if (d > tolerance) { aspects |= ChangeAspect.Moved; r.MoveDistance = d; r.MoveVector = v; }
                if (sizeChanged) aspects |= ChangeAspect.Geometry;
            }
            if (r.OldLength.HasValue && r.NewLength.HasValue && Math.Abs(r.NewLength.Value - r.OldLength.Value) > tolerance) aspects |= ChangeAspect.Geometry;
            r.Aspects = aspects;
            return r;
        }
    }
}
