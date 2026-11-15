using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ProjectPerseus.models;
using ProjectPerseus.util;

namespace ProjectPerseus.mapping
{
    /// <summary>A consultant value after a mapping's transform: Changed = a step changed it (now text); Problem = not written.</summary>
    public class TransformResult
    {
        public SourceValue Value;
        public bool Changed;
        public string Problem;
    }

    /// <summary>
    /// Parameter mapping transforms (stage 5 phase 4), pure (tested in ProjectPerseusTests/ParamCompatTest.cs): constant, default for
    /// empty, lookup table, expression (util/FilterEvaluator.TryCompute, names value / number / empty), prefix / suffix. A changed value
    /// is text and goes through mapping/ParamCompat as from a text parameter; an unchanged one keeps its own kind (a length copied as a
    /// length stays exact).
    /// </summary>
    public static class ParamTransforms
    {
        public static bool IsIdentity(ParamTransform t) =>
            t == null || (t.Constant == null && string.IsNullOrEmpty(t.Default) && (t.Lookup == null || !t.Lookup.Any(Valid)) &&
                          string.IsNullOrWhiteSpace(t.Expression) && string.IsNullOrEmpty(t.Prefix) && string.IsNullOrEmpty(t.Suffix));

        private static bool Valid(LookupEntry e) => e != null && !string.IsNullOrEmpty(e.From);

        private static readonly Regex RowRef = new Regex(@"\brow\b", RegexOptions.Compiled);

        /// <summary>The expression reads other consultant parameters (row["..."]): callers pass the consultant element's row.</summary>
        public static bool UsesRow(ParamTransform t) => t != null && t.Constant == null && !string.IsNullOrWhiteSpace(t.Expression) && RowRef.IsMatch(t.Expression);

        /// <summary>No consultant parameter: the expression alone makes the value (typically from row["..."]).</summary>
        public static bool ExpressionOnly(ParamRef src, ParamTransform t) => src == null && t != null && t.Constant == null && !string.IsNullOrWhiteSpace(t.Expression);

        /// <summary>Steps that change every (non-empty) value.</summary>
        private static bool AlwaysText(ParamTransform t) =>
            t != null && (t.Constant != null || !string.IsNullOrWhiteSpace(t.Expression) || !string.IsNullOrEmpty(t.Prefix) || !string.IsNullOrEmpty(t.Suffix) ||
                          (t.LookupStrict && t.Lookup != null && t.Lookup.Any(Valid)));

        /// <summary>The consultant parameter as seen after a changing step: text (constants: "computed", so no type-from-instance warning).</summary>
        public static ParamRef TextRef(ParamRef src, ParamTransform t) => new ParamRef
        {
            Name = t?.Constant != null ? "constant" : src?.Name ?? "expression", Kind = ParamCompat.Text, Storage = "String",
            IsType = src?.IsType ?? false, Computed = t?.Constant != null ? "constant" : src == null ? "expression" : src.Computed,
        };

        /// <summary>Short description for the mapping list ("lookup 4 · prefix").</summary>
        public static string Describe(ParamTransform t)
        {
            if (IsIdentity(t)) return "";
            var parts = new List<string>();
            if (t.Constant != null) return $"constant '{t.Constant}'";
            if (UsesRow(t)) parts.Add("reads other parameters");
            if (!string.IsNullOrEmpty(t.Default)) parts.Add($"default '{t.Default}'");
            int n = t.Lookup?.Count(Valid) ?? 0;
            if (n > 0) parts.Add($"lookup {n}{(t.LookupStrict ? " (strict)" : "")}");
            if (!string.IsNullOrWhiteSpace(t.Expression)) parts.Add("expression");
            if (!string.IsNullOrEmpty(t.Prefix)) parts.Add($"prefix '{t.Prefix}'");
            if (!string.IsNullOrEmpty(t.Suffix)) parts.Add($"suffix '{t.Suffix}'");
            return string.Join(" · ", parts);
        }

        private static bool IsEmpty(ParamRef src, SourceValue v) =>
            v == null || !v.HasValue || ((src == null || src.Kind == ParamCompat.Text || src.Kind == ParamCompat.Element || !v.Number.HasValue) && string.IsNullOrEmpty(v.Text));

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>The transform applied to one consultant value.</summary>
        /// <param name="row">The consultant element's parameters by name (UsesRow), else null.</param>
        public static TransformResult Apply(ParamRef src, ParamTransform t, SourceValue v, Dictionary<string, string> row = null)
        {
            var unchanged = new TransformResult { Value = v ?? new SourceValue() };
            if (IsIdentity(t)) return unchanged;
            if (t.Constant != null) return Text(t.Constant);
            string cur;
            double? number = null;
            bool changed = false;
            if (ExpressionOnly(src, t))
            {
                if (!FilterEvaluator.TryCompute(t.Expression, row, new Dictionary<string, object> { { "value", "" }, { "number", 0.0 }, { "empty", true } }, out object r0, out string e0))
                    return new TransformResult { Value = v, Problem = "expression: " + e0 };
                cur = r0 is double d0 ? N(d0) : FilterEvaluator.PyStr(r0);
                if (!string.IsNullOrEmpty(t.Prefix) || !string.IsNullOrEmpty(t.Suffix)) cur = (t.Prefix ?? "") + cur + (t.Suffix ?? "");
                return Text(cur);
            }
            if (IsEmpty(src, v))
            {
                if (string.IsNullOrEmpty(t.Default)) return unchanged;   // empty stays empty (nothing to look up / prefix)
                cur = t.Default; changed = true;
            }
            else
            {
                cur = v.Text ?? (v.Number.HasValue ? N(v.Number.Value) : "");
                if (src?.Kind == ParamCompat.Measure) number = v.Display ?? v.Number;
                else if (src?.Kind != ParamCompat.Text && src?.Kind != ParamCompat.Element) number = v.Number;
            }
            var table = t.Lookup?.Where(Valid).ToList() ?? new List<LookupEntry>();
            if (table.Count > 0)
            {
                var hit = table.FirstOrDefault(e => string.Equals(e.From.Trim(), (cur ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit != null) { cur = hit.To ?? ""; changed = true; number = null; }
                else if (t.LookupStrict) return new TransformResult { Value = v, Problem = $"'{cur}' isn't in the lookup table" };
            }
            if (!string.IsNullOrWhiteSpace(t.Expression))
            {
                if (number == null && ParamCompat.TryParseNumber(cur, out double parsed)) number = parsed;
                var names = new Dictionary<string, object>
                {
                    { "value", cur ?? "" }, { "number", number ?? 0.0 }, { "empty", string.IsNullOrEmpty(cur) },
                };
                if (!FilterEvaluator.TryCompute(t.Expression, row, names, out object r, out string err))
                    return new TransformResult { Value = v, Problem = "expression: " + err };
                cur = r is double d ? N(d) : FilterEvaluator.PyStr(r);
                changed = true;
            }
            if (!string.IsNullOrEmpty(t.Prefix) || !string.IsNullOrEmpty(t.Suffix)) { cur = (t.Prefix ?? "") + cur + (t.Suffix ?? ""); changed = true; }
            return changed ? Text(cur) : unchanged;
        }

        private static string RowLabel(Dictionary<string, string> row, int i)
        {
            if (row == null) return "(expression)";
            foreach (var k in new[] { "Mark", "Consultant element id" })
                if (row.TryGetValue(k, out var s) && !string.IsNullOrEmpty(s)) return k == "Mark" ? "Mark " + s : "#" + s;
            return "element " + (i + 1);
        }

        private static TransformResult Text(string s) => new TransformResult { Changed = true, Value = new SourceValue { HasValue = true, Text = s ?? "" } };

        private static ParamRef Effective(ParamRef src, ParamTransform t, TransformResult r) => r.Changed ? TextRef(src, t) : src;

        /// <summary>Compatibility of a mapping with its transform: the consultant kind, the text kind after a changing step, or both.</summary>
        public static Compat Check(ParamRef src, ParamTransform t, ParamRef dst)
        {
            if (IsIdentity(t)) return ParamCompat.Check(src, dst);
            if (src == null && t.Constant == null && !ExpressionOnly(src, t)) return Compat.Error("Pick a consultant parameter (or use a constant / an expression).");
            if (t.Constant != null || src == null || AlwaysText(t))
            {
                var c = ParamCompat.Check(TextRef(src, t), dst);
                if (c.Level == CompatLevel.Error || src == null || t.Constant != null) return c;
                if (dst != null && dst.IsType && !src.IsType && src.Computed == null && c.Level == CompatLevel.Ok)
                    return ParamCompat.Check(src, new ParamRef { Kind = ParamCompat.Text, IsType = true, Name = dst.Name });   // keeps the type-from-instance warning
                return c;
            }
            var own = ParamCompat.Check(src, dst);
            if (own.Level == CompatLevel.Error) return own;
            var text = ParamCompat.Check(TextRef(src, t), dst);
            if (text.Level <= own.Level) return own;
            return new Compat { Level = text.Level, Message = own.Message + " Transformed values: " + text.Message };
        }

        public static ConvertedValue Convert(ParamRef src, ParamTransform t, ParamRef dst, SourceValue v, Dictionary<string, string> row = null)
        {
            var r = Apply(src, t, v, row);
            if (r.Problem != null) return new ConvertedValue { Problem = r.Problem };
            return ParamCompat.Convert(Effective(src, t, r), dst, r.Value);
        }

        public static TypedValue TargetValue(ParamRef src, ParamTransform t, ParamRef dst, SourceValue v, Dictionary<string, string> row = null)
        {
            var r = Apply(src, t, v, row);
            if (r.Problem != null) return new TypedValue { Problem = r.Problem, Shown = r.Problem };
            return ParamCompat.TargetValue(Effective(src, t, r), dst, r.Value);
        }

        /// <summary>Canonical consultant value after the transform (Link Sync baselines: a new lookup table re-opens a decision).</summary>
        public static string SourceCanon(ParamRef src, ParamTransform t, SourceValue v, Dictionary<string, string> row = null)
        {
            var r = Apply(src, t, v, row);
            return r.Changed && r.Problem == null ? "t:" + (r.Value.Text ?? "") : ParamCompat.SourceCanon(v);
        }

        /// <summary>Preview of a mapping with its transform on the consultant values (a constant: one sample).</summary>
        /// <param name="rows">Per value, the consultant element's parameters (expressions reading row["..."]); null = none.</param>
        public static ParamPreview Preview(ParamRef src, ParamTransform t, ParamRef dst, IList<SourceValue> values, int samples = 6, IList<Dictionary<string, string>> rows = null)
        {
            if (t?.Constant != null) values = new List<SourceValue> { new SourceValue() };
            else if (ExpressionOnly(src, t)) values = Enumerable.Range(0, Math.Max(1, rows?.Count ?? 0)).Select(_ => new SourceValue()).ToList();
            var p = new ParamPreview();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            int i = -1;
            foreach (var v in values ?? new List<SourceValue>())
            {
                i++;
                p.Total++;
                var row = rows != null && i < rows.Count ? rows[i] : null;
                var c = Convert(src, t, dst, v, row);
                if (c.Empty) p.Empty++;
                else if (c.Ok) p.Converted++;
                else { p.Failing++; if (p.Problems.Count < 4 && !p.Problems.Contains(c.Problem)) p.Problems.Add(c.Problem); }
                string before = t?.Constant != null ? "(constant)" : ExpressionOnly(src, t) ? RowLabel(row, i) : v?.HasValue == true ? (string.IsNullOrEmpty(v.Text) ? "(empty)" : v.Text) : "(empty)";
                distinct.Add(c.Ok ? c.Shown ?? "" : "\0" + c.Problem);
                if (p.Samples.Count < samples && (!c.Empty || !IsIdentity(t)) && !p.Samples.Any(x => x.Item1 == before))
                    p.Samples.Add(Tuple.Create(before, c.Ok ? c.Shown : "✗ " + c.Problem));
            }
            p.Distinct = distinct.Count;
            return p;
        }
    }
}
