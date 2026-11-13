using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>What Link Sync found for one of our families / one consultant element.</summary>
    public enum SyncKind
    {
        Replaced,         // consultant element deleted, replacement linked: point ours at it
        ConsultantType,   // the consultant element changed type
        MappingType,      // the rule now says another of our types
        Moved,            // consultant moved / rule offset changed: ours isn't where the mapping puts it
        Match,            // one of ours, unrecorded, sits at the expected spot: link it
        New,              // mapped consultant element with none of ours: place
        Orphaned,         // consultant element gone, no replacement: delete ours (local orphan)
        Unlinked,         // one of ours of a mapped type, never linked, no consultant element near it: delete ours (local orphan)
        OursDeleted,      // ours was linked to this consultant element and has been deleted here: place again (linked orphan)
        KeptByHand,       // ours moved by hand (consultant + rule unchanged): rejects the consultant position
        KeptRejected,     // a change the user rejected
        Unmapped,         // its consultant type isn't mapped any more
        InSync,
    }

    /// <summary>A consultant element (host coordinates).</summary>
    public class SyncSource
    {
        public string Uid;
        public long Id;
        public string TypeKey;     // FamilyMapRule.Key(category, family, type)
        public string Category;
        public Frame Frame;
    }

    /// <summary>One of our family instances.</summary>
    public class SyncOurs
    {
        public long Id;
        public string Uid;
        public string TypeUid;
        public string TypeName;
        public Frame Frame;
        public MappedFamilyData Record;   // null = not mapped (Match candidates)
    }

    public class SyncItem
    {
        public SyncKind Kind;
        public string LinkUid;
        /// <summary>The consultant element (for Replaced: the old one; NewSourceUid is the replacement).</summary>
        public string SourceUid;
        public long SourceId;
        public string NewSourceUid;
        public long NewSourceId;
        public string SourceTypeKey;
        public string Category;
        public long OursId;              // 0 = none (New)
        public string OursUid;           // set by mapping/LinkSync (notes are keyed by it)
        /// <summary>Who last changed ours in this model / the consultant element in its model (worksharing; set by mapping/LinkSync).</summary>
        public string OursEditedBy, SourceEditedBy;
        /// <summary>Model markers (host feet, set by mapping/LinkSync): the box to draw and, for moves, where ours is now (ghost + arrow).</summary>
        public double[] ShowMin, ShowMax, GhostMin, GhostMax;
        public string OursType;
        public string RuleId;            // rule to apply (place / type / move)
        public string TargetUid;         // our type to change to / place
        public string TargetName;
        public Frame Expected;           // where the mapping puts ours
        public string Detail;

        public bool NeedsDecision => LinkSyncPlanner.NeedsDecision(Kind);
        public string Key => $"{Kind}|{LinkUid}|{OursId}|{SourceUid}";
    }

    public class SyncInput
    {
        public string LinkUid;
        /// <summary>Consultant elements of mapped types plus those our records point at (any type).</summary>
        public List<SyncSource> Sources = new List<SyncSource>();
        /// <summary>Our families recorded against this link.</summary>
        public List<SyncOurs> Ours = new List<SyncOurs>();
        /// <summary>Our unrecorded families of mapped target types (Match candidates).</summary>
        public List<SyncOurs> Unrecorded = new List<SyncOurs>();
        public List<FamilyMapRule> Rules = new List<FamilyMapRule>();
        public HashSet<string> HiddenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>old consultant uid -> current uid (federator/ReplacementLinks.Map).</summary>
        public IDictionary<string, string> ReplacementMap = new Dictionary<string, string>();
        /// <summary>Consultant uids whose New / Match / OursDeleted proposal was rejected.</summary>
        public HashSet<string> RejectedNew = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Consultant uids that have been linked to one of ours (mapping/LinkSyncNotes.Connected): none of ours now = ours was deleted.</summary>
        public HashSet<string> Connected = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Our unrecorded families whose Unlinked (orphan) proposal was rejected (our UniqueIds; matched against SyncOurs.Uid).</summary>
        public HashSet<string> KeptUnlinked = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Link Sync's decision logic, pure (no Revit API; tested in ProjectPerseusTests/LinkSyncPlannerTest.cs): compares
    /// the live link (consultant elements + frames) with our tracked families and the mapping rules, and lists every
    /// change to approve or reject, one item per change (one of ours can have a type change AND a move). State-based:
    /// comparisons only explain the delta; replacement links make ours follow a deleted-and-replaced element.
    /// Moves: ours vs the expected frame (consultant frame + rule offset / rotation) within the rule's XY / Z tolerance.
    /// Beyond it: a rejected expected frame -> Kept (rejected); expected unchanged since the last accept (= ours was
    /// moved by hand) -> Kept (by hand); else Moved.
    /// </summary>
    public static class LinkSyncPlanner
    {
        public static bool NeedsDecision(SyncKind k) =>
            k == SyncKind.Replaced || k == SyncKind.ConsultantType || k == SyncKind.MappingType || k == SyncKind.Moved ||
            k == SyncKind.Match || k == SyncKind.New || k == SyncKind.Orphaned || k == SyncKind.Unlinked || k == SyncKind.OursDeleted;

        /// <summary>Orphans: ours without a consultant element (local) or a consultant element whose linked family of ours was deleted (linked).</summary>
        public static bool IsOrphan(SyncKind k) => k == SyncKind.Orphaned || k == SyncKind.Unlinked || k == SyncKind.OursDeleted;

        /// <summary>Approving deletes our family.</summary>
        public static bool ApproveDeletes(SyncKind k) => k == SyncKind.Orphaned || k == SyncKind.Unlinked;

        /// <summary>
        /// An existing family of ours (not linked yet) counts as at a consultant element within this distance of where the mapping
        /// puts ours (or the rule's tolerance, if larger); a looser match is linked, then flagged as moved if beyond tolerance.
        /// </summary>
        public const double MatchRadiusMm = 300;

        public static string Label(SyncKind k)
        {
            switch (k)
            {
                case SyncKind.Replaced: return "Replaced";
                case SyncKind.ConsultantType: return "Consultant type changed";
                case SyncKind.MappingType: return "Mapping type changed";
                case SyncKind.Moved: return "Moved";
                case SyncKind.Match: return "Match (link existing)";
                case SyncKind.New: return "New (place)";
                case SyncKind.Orphaned: return "Orphaned (consultant deleted)";
                case SyncKind.Unlinked: return "Orphaned (no consultant element)";
                case SyncKind.OursDeleted: return "Orphaned (ours deleted)";
                case SyncKind.KeptByHand: return "Kept (edited by hand)";
                case SyncKind.KeptRejected: return "Kept (rejected)";
                case SyncKind.Unmapped: return "Not mapped";
                default: return "In sync";
            }
        }

        /// <summary>The Perseus_LinkStatus text for one kind.</summary>
        public static string Status(SyncKind k)
        {
            switch (k)
            {
                case SyncKind.Replaced: return "Consultant replaced";
                case SyncKind.ConsultantType: return "Consultant type changed";
                case SyncKind.MappingType: return "Mapping type changed";
                case SyncKind.Moved: return "Consultant moved";
                case SyncKind.Orphaned: return "Consultant deleted";
                case SyncKind.Unlinked: return "No consultant element";
                case SyncKind.KeptByHand: return "Kept: edited by hand";
                case SyncKind.KeptRejected: return "Kept: change rejected";
                case SyncKind.Unmapped: return "Not mapped";
                default: return "In sync";
            }
        }

        /// <summary>"Family : Type" from a rule key.</summary>
        public static string Pretty(string typeKey)
        {
            if (string.IsNullOrEmpty(typeKey)) return "?";
            var p = typeKey.Split('|');
            return p.Length >= 3 ? $"{p[1]} : {p[2]}" : typeKey;
        }

        public static List<SyncItem> Plan(SyncInput input)
        {
            var items = new List<SyncItem>();
            var rules = input.Rules.Where(r => r.Enabled && !string.IsNullOrEmpty(r.TargetSymbolUid) && !input.HiddenCategories.Contains(r.SourceCategory ?? ""))
                                   .GroupBy(r => r.SourceKey).ToDictionary(g => g.Key, g => g.Last());
            var rulesById = input.Rules.Where(r => r.Id != null).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Last());
            var src = new Dictionary<string, SyncSource>(StringComparer.Ordinal);
            foreach (var s in input.Sources) if (s.Uid != null) src[s.Uid] = s;
            var covered = new HashSet<string>(StringComparer.Ordinal);

            foreach (var o in input.Ours)
            {
                var r = o.Record;
                if (r == null) continue;
                SyncItem Item(SyncKind k, SyncSource s) => new SyncItem
                {
                    Kind = k, LinkUid = input.LinkUid, SourceUid = s?.Uid ?? r.SourceUid, SourceId = s?.Id ?? ParseId(r.SourceId), SourceTypeKey = s?.TypeKey ?? r.SourceTypeKey,
                    Category = s?.Category, OursId = o.Id, OursType = o.TypeName, RuleId = r.RuleId,
                };

                // Replacement link: ours follows the element that replaced its consultant element.
                string cur = r.SourceUid;
                SyncSource now = null;
                if (cur != null && input.ReplacementMap != null && input.ReplacementMap.TryGetValue(cur, out var repl) && repl != cur
                    && repl != r.RejectedReplacement && src.TryGetValue(repl, out now))
                {
                    var it = Item(SyncKind.Replaced, null);
                    it.Category = now.Category;
                    it.NewSourceUid = repl; it.NewSourceId = now.Id;
                    it.Detail = $"Consultant element {r.SourceId} was deleted and replaced by {now.Id} ({Pretty(now.TypeKey)}): ours follows it.";
                    items.Add(it);
                    cur = repl;
                }
                if (now == null && (cur == null || !src.TryGetValue(cur, out now)))
                {
                    var it = Item(r.RejectedGone ? SyncKind.KeptRejected : SyncKind.Orphaned, null);
                    it.Detail = r.RejectedGone ? $"Consultant element {r.SourceId} is gone; keeping ours was chosen."
                                               : $"Consultant element {r.SourceId} is gone and nothing replaced it. Approve = delete ours.";
                    items.Add(it);
                    continue;
                }
                covered.Add(now.Uid);

                rulesById.TryGetValue(r.RuleId ?? "", out var oldRule);
                string baseType = r.SourceTypeKey ?? oldRule?.SourceKey ?? now.TypeKey;
                rules.TryGetValue(now.TypeKey, out var rule);
                bool typeChanged = !string.Equals(baseType, now.TypeKey, StringComparison.Ordinal);
                if (typeChanged)
                {
                    if (r.RejectedSourceType != now.TypeKey)
                    {
                        var it = Item(SyncKind.ConsultantType, now);
                        it.RuleId = rule?.Id; it.TargetUid = rule?.TargetSymbolUid; it.TargetName = rule?.TargetName;
                        it.Detail = $"The consultant changed {Pretty(baseType)} to {Pretty(now.TypeKey)}. " +
                                    (rule == null ? "That type isn't mapped: map it in Family mapping to approve."
                                     : rule.TargetSymbolUid == o.TypeUid ? "Ours stays the same type." : $"Approve = ours becomes {rule.TargetName}.");
                        items.Add(it);
                    }
                }
                else if (rule == null)
                {
                    var it = Item(SyncKind.Unmapped, now);
                    it.Detail = $"{Pretty(now.TypeKey)} isn't mapped any more (rule cleared or category turned off).";
                    items.Add(it);
                }
                else if (o.TypeUid != rule.TargetSymbolUid && r.RejectedTargetUid != rule.TargetSymbolUid)
                {
                    var it = Item(SyncKind.MappingType, now);
                    it.RuleId = rule.Id; it.TargetUid = rule.TargetSymbolUid; it.TargetName = rule.TargetName;
                    it.Detail = $"The mapping now uses {rule.TargetName}; ours is {o.TypeName}. Approve = change its type.";
                    items.Add(it);
                }

                // Position against the rule of its current consultant type (else the rule it was placed by).
                var posRule = rule ?? oldRule;
                if (posRule == null || o.Frame == null || now.Frame == null) continue;
                var expected = MappingMath.Place(now.Frame, posRule.OffsetMm, posRule.RotationDeg);
                var d = MappingMath.Delta(o.Frame, expected);
                double tx = posRule.ToleranceXyMm, tz = posRule.ToleranceZMm;
                if (MappingMath.Within(d, tx, tz))
                {
                    if (!items.Any(i => i.OursId == o.Id)) { var ok = Item(SyncKind.InSync, now); ok.Detail = "Where the mapping puts it."; items.Add(ok); }
                    continue;
                }
                var rejected = MappingMath.Unpack(r.RejectedExpected);
                if (rejected != null && MappingMath.Within(MappingMath.Delta(rejected, expected), tx, tz))
                {
                    var it = Item(SyncKind.KeptRejected, now);
                    it.Expected = expected; it.RuleId = posRule.Id;
                    it.Detail = $"Moving it was rejected: {MappingMath.Describe(d)} from where the mapping puts it.";
                    items.Add(it);
                    continue;
                }
                var oursBase = MappingMath.Unpack(r.OursFrame);
                if (oursBase != null && MappingMath.Within(MappingMath.Delta(oursBase, expected), tx, tz))
                {
                    var it = Item(SyncKind.KeptByHand, now);
                    it.Expected = expected; it.RuleId = posRule.Id;
                    it.Detail = $"Moved by hand {MappingMath.Describe(d)} from the consultant position (keeps its own position). Approve = move it back.";
                    items.Add(it);
                    continue;
                }
                var mv = Item(SyncKind.Moved, now);
                mv.Expected = expected; mv.RuleId = posRule.Id;
                var why = new List<string>();
                var srcBase = MappingMath.Unpack(r.SourceFrame);
                if (srcBase != null)
                {
                    var sd = MappingMath.Delta(now.Frame, srcBase);
                    if (!MappingMath.Within(sd, tx, tz)) why.Add($"the consultant element moved {MappingMath.Describe(sd)}");
                    if (oursBase != null && !MappingMath.Within(MappingMath.Delta(MappingMath.Place(srcBase, posRule.OffsetMm, posRule.RotationDeg), oursBase), tx, tz))
                        why.Add("the mapping's offset / rotation changed");
                    if (oursBase != null && !MappingMath.Within(MappingMath.Delta(o.Frame, oursBase), tx, tz)) why.Add("ours was also moved by hand");
                }
                mv.Detail = (why.Count > 0 ? char.ToUpper(why[0][0]) + string.Join("; ", why).Substring(1) + ". " : "") +
                            $"Ours is {MappingMath.Describe(d)} from where the mapping puts it. Approve = move ours.";
                items.Add(mv);
            }

            // Consultant elements of mapped types without one of ours: link an unrecorded one at the spot, else place.
            var todo = input.Sources.Where(s => !covered.Contains(s.Uid) && rules.ContainsKey(s.TypeKey ?? "") && !input.RejectedNew.Contains(s.Uid) && s.Frame != null).ToList();
            var pairs = new List<Tuple<double, int, int>>();
            var expectedOf = new Frame[todo.Count];
            for (int i = 0; i < todo.Count; i++)
            {
                var rule = rules[todo[i].TypeKey];
                expectedOf[i] = MappingMath.Place(todo[i].Frame, rule.OffsetMm, rule.RotationDeg);
                for (int j = 0; j < input.Unrecorded.Count; j++)
                {
                    var u = input.Unrecorded[j];
                    if (u.TypeUid != rule.TargetSymbolUid || u.Frame == null) continue;
                    var d = MappingMath.Delta(u.Frame, expectedOf[i]);
                    if (d.XyMm <= Math.Max(rule.ToleranceXyMm, MatchRadiusMm) && d.ZMm <= Math.Max(rule.ToleranceZMm, MatchRadiusMm)) pairs.Add(Tuple.Create(d.TotalMm, i, j));
                }
            }
            var usedS = new HashSet<int>();
            var usedU = new HashSet<int>();
            foreach (var p in pairs.OrderBy(x => x.Item1))
            {
                if (usedS.Contains(p.Item2) || usedU.Contains(p.Item3)) continue;
                usedS.Add(p.Item2); usedU.Add(p.Item3);
                var s = todo[p.Item2]; var u = input.Unrecorded[p.Item3]; var rule = rules[s.TypeKey];
                items.Add(new SyncItem
                {
                    Kind = SyncKind.Match, LinkUid = input.LinkUid, SourceUid = s.Uid, SourceId = s.Id, SourceTypeKey = s.TypeKey, Category = s.Category,
                    OursId = u.Id, OursType = u.TypeName, RuleId = rule.Id, TargetUid = rule.TargetSymbolUid, TargetName = rule.TargetName,
                    Expected = expectedOf[p.Item2],
                    Detail = $"Ours ({u.TypeName}, not linked yet) is {p.Item1:N0} mm from where the mapping puts it. Approve = link it to this consultant element" +
                             (MappingMath.Within(MappingMath.Delta(u.Frame, expectedOf[p.Item2]), rule.ToleranceXyMm, rule.ToleranceZMm) ? "." : " (then listed as moved: it's beyond the tolerance)."),
                });
            }
            for (int i = 0; i < todo.Count; i++)
            {
                if (usedS.Contains(i)) continue;
                var s = todo[i]; var rule = rules[s.TypeKey];
                bool deleted = input.Connected.Contains(s.Uid);
                items.Add(new SyncItem
                {
                    Kind = deleted ? SyncKind.OursDeleted : SyncKind.New, LinkUid = input.LinkUid, SourceUid = s.Uid, SourceId = s.Id, SourceTypeKey = s.TypeKey, Category = s.Category,
                    RuleId = rule.Id, TargetUid = rule.TargetSymbolUid, TargetName = rule.TargetName, Expected = expectedOf[i],
                    Detail = deleted ? $"Ours at {Pretty(s.TypeKey)} was deleted from this model; the consultant element is still there. Approve = place {rule.TargetName} again, reject = leave it deleted."
                                     : $"{Pretty(s.TypeKey)} has none of ours. Approve = place {rule.TargetName}.",
                });
            }

            // Ours of mapped types never linked, with no consultant element near: local orphans.
            var activeTargets = new HashSet<string>(rules.Values.Select(r => r.TargetSymbolUid), StringComparer.Ordinal);
            for (int j = 0; j < input.Unrecorded.Count; j++)
            {
                if (usedU.Contains(j)) continue;
                var u = input.Unrecorded[j];
                if (u.TypeUid == null || !activeTargets.Contains(u.TypeUid)) continue;
                if (u.Uid != null && input.KeptUnlinked.Contains(u.Uid)) continue;
                items.Add(new SyncItem
                {
                    Kind = SyncKind.Unlinked, LinkUid = input.LinkUid, OursId = u.Id, OursType = u.TypeName,
                    Category = input.Rules.FirstOrDefault(r => r.TargetSymbolUid == u.TypeUid)?.SourceCategory,
                    Detail = $"Ours ({u.TypeName}) is a mapped type but no consultant element is within {MatchRadiusMm:N0} mm of it. Approve = delete ours, reject = keep it.",
                });
            }
            return items;
        }

        /// <summary>
        /// Across links (each planned on its own): one of ours not linked yet is only an orphan if no link matched it, and is
        /// listed once.
        /// </summary>
        public static List<SyncItem> Reconcile(List<SyncItem> items)
        {
            var matched = new HashSet<long>(items.Where(i => i.Kind == SyncKind.Match).Select(i => i.OursId));
            var seen = new HashSet<long>();
            return items.Where(i => i.Kind != SyncKind.Unlinked || (!matched.Contains(i.OursId) && seen.Add(i.OursId))).ToList();
        }

        private static long ParseId(string s) => long.TryParse(s, out var v) ? v : 0;
    }
}
