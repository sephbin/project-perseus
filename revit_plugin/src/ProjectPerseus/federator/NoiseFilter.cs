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

        public static bool IsNoise(ElementChange c, NoiseRules rules, HashSet<string> parameters)
        {
            if (c == null || rules == null || c.Kind != ChangeKind.Modified) return false;
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
