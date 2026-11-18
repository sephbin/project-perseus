using System;
using System.Collections.Generic;
using System.Globalization;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Target value of an Auto Parameters value rule: the pattern filled in by the resolver, then the optional
    /// transform expression (util/FilterEvaluator.TryCompute; names: value = the filled-in pattern, row["Level.Name"] =
    /// any token the pattern uses). Pure, no Revit dependencies; tested in ProjectPerseusTests/IdPatternTest.cs.
    /// </summary>
    public static class AutoValue
    {
        public enum Outcome
        {
            Ok,
            /// <summary>A token has no value for this element (no room, parameter missing...): the target is "" (cleared).</summary>
            Unresolved,
            /// <summary>The transform failed; nothing should be written.</summary>
            TransformError,
        }

        public static Outcome Compute(IdPattern pattern, string transform, Func<string, string> resolve,
                                      out string value, out string error)
        {
            value = "";
            error = null;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in pattern.Tokens)
            {
                string v = resolve(t);
                if (string.IsNullOrEmpty(v)) { error = t; return Outcome.Unresolved; }
                row[t] = v;
            }

            string rendered = pattern.Render(t => row.TryGetValue(t, out var v) ? v : "", 0);
            if (string.IsNullOrWhiteSpace(transform)) { value = rendered; return Outcome.Ok; }

            var names = new Dictionary<string, object> { { "value", rendered } };
            if (!FilterEvaluator.TryCompute(transform, row, names, out object r, out error)) return Outcome.TransformError;
            value = r is double d ? Number(d) : FilterEvaluator.PyStr(r);
            return Outcome.Ok;
        }

        private static string Number(double d) =>
            Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15
                ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
                : d.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
