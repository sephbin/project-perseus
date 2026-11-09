using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>Is a change only noise under the link's NoiseRules? Pure: unit-tested in ProjectPerseusTests/ChangeTriageTest.cs.</summary>
    public static class NoiseFilter
    {
        public static HashSet<string> ParameterSet(NoiseRules rules) =>
            new HashSet<string>(rules?.Parameters ?? new List<string>(), StringComparer.Ordinal);

        /// <summary>
        /// The change with its noise removed: noise parameters dropped from ParameterChanges (and the Parameters aspect
        /// when none are left), a move shorter than MinMoveMm dropped from the aspects. Returns the same instance when
        /// nothing is noise, a copy when something was removed, and null when nothing real is left (the change is noise).
        /// The stored change is never modified.
        /// </summary>
        public static ElementChange Effective(ElementChange c, NoiseRules rules, HashSet<string> parameters)
        {
            if (c == null || rules == null) return c;
            if (IsNoiseCategory(c, rules)) return null;
            if (c.Kind != ChangeKind.Modified) return c;
            var aspects = c.Aspects;
            List<ParameterChange> kept = c.ParameterChanges;
            if (aspects.HasFlag(ChangeAspect.Parameters) && c.ParameterChanges != null && parameters.Count > 0)
            {
                kept = c.ParameterChanges.Where(p => !parameters.Contains(p.Name)).ToList();
                if (kept.Count == 0) aspects &= ~ChangeAspect.Parameters;
            }
            bool smallMove = aspects.HasFlag(ChangeAspect.Moved) && rules.MinMoveMm > 0 && c.MoveDistance * 304.8 < rules.MinMoveMm;
            if (smallMove) aspects &= ~ChangeAspect.Moved;
            if (aspects == ChangeAspect.None) return null;
            if (aspects == c.Aspects && ReferenceEquals(kept, c.ParameterChanges)) return c;
            if (aspects == c.Aspects && kept != null && c.ParameterChanges != null && kept.Count == c.ParameterChanges.Count) return c;
            var e = Copy(c);
            e.Aspects = aspects;
            e.ParameterChanges = kept ?? new List<ParameterChange>();
            if (smallMove) { e.MoveDistance = 0; e.MoveVector = null; }
            return e;
        }

        /// <summary>Parameter names of c that the rules treat as noise.</summary>
        public static List<string> NoiseParameters(ElementChange c, HashSet<string> parameters) =>
            c?.ParameterChanges == null ? new List<string>()
                : c.ParameterChanges.Where(p => parameters.Contains(p.Name)).Select(p => p.Name).ToList();

        private static ElementChange Copy(ElementChange c) => new ElementChange
        {
            Kind = c.Kind, Aspects = c.Aspects, UniqueId = c.UniqueId, Id = c.Id, Category = c.Category, Family = c.Family,
            Type = c.Type, Level = c.Level, OldType = c.OldType, OldLevel = c.OldLevel, MoveDistance = c.MoveDistance,
            OldMin = c.OldMin, OldMax = c.OldMax, NewMin = c.NewMin, NewMax = c.NewMax, ParameterChanges = c.ParameterChanges,
            MoveVector = c.MoveVector, OldLength = c.OldLength, NewLength = c.NewLength, OldVolume = c.OldVolume,
            NewVolume = c.NewVolume, Signature = c.Signature, Replacements = c.Replacements,
        };

        /// <summary>Is the change's category one of the rules' noise categories (any kind of change)?</summary>
        public static bool IsNoiseCategory(ElementChange c, NoiseRules rules) =>
            c != null && rules?.Categories != null && rules.Categories.Count > 0
            && rules.Categories.Contains(c.Category ?? "", StringComparer.OrdinalIgnoreCase);

        public static bool IsNoise(ElementChange c, NoiseRules rules, HashSet<string> parameters)
        {
            if (c == null || rules == null) return false;
            if (IsNoiseCategory(c, rules)) return true;
            if (c.Kind != ChangeKind.Modified) return false;
            var rest = c.Aspects;
            if (rest.HasFlag(ChangeAspect.Parameters) && c.ParameterChanges != null && c.ParameterChanges.Count > 0
                && c.ParameterChanges.All(p => parameters.Contains(p.Name)))
                rest &= ~ChangeAspect.Parameters;
            if (rest.HasFlag(ChangeAspect.Moved) && rules.MinMoveMm > 0 && c.MoveDistance * 304.8 < rules.MinMoveMm)
                rest &= ~ChangeAspect.Moved;
            return rest == ChangeAspect.None;
        }
    }
}
