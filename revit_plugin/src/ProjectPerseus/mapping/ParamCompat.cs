using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>A consultant parameter value as read for the preview (mapping/ParamDiscovery).</summary>
    public class SourceValue
    {
        public bool HasValue;
        /// <summary>As Revit shows it (AsValueString / AsString / element name).</summary>
        public string Text;
        /// <summary>Double: internal units; Integer / YesNo: the integer.</summary>
        public double? Number;
        /// <summary>Measure: the value in the project's display units (consultant model).</summary>
        public double? Display;
    }

    /// <summary>What happens to one value: the local value shown, or why it doesn't map.</summary>
    public class ConvertedValue
    {
        public bool Ok, Empty;
        public string Shown;
        public string Problem;
    }

    /// <summary>A value ready to write into a local parameter (ParamCompat.TargetValue).</summary>
    public class TypedValue
    {
        public bool Ok, Empty;
        public string Problem, Shown;
        public string Text;
        public int? Int;
        public double? Internal, Display;
    }

    public enum CompatLevel { Ok, Warn, Error }

    public class Compat
    {
        public CompatLevel Level;
        public string Message;
        public static Compat Ok(string m) => new Compat { Level = CompatLevel.Ok, Message = m };
        public static Compat Warn(string m) => new Compat { Level = CompatLevel.Warn, Message = m };
        public static Compat Error(string m) => new Compat { Level = CompatLevel.Error, Message = m };
        public string Symbol => Level == CompatLevel.Ok ? "✓" : Level == CompatLevel.Warn ? "⚠" : "✗";
    }

    /// <summary>Counts + samples of a mapping applied to the consultant values (preview).</summary>
    public class ParamPreview
    {
        public int Total, Empty, Converted, Failing, Distinct;
        public List<Tuple<string, string>> Samples = new List<Tuple<string, string>>();   // before, after
        public List<string> Problems = new List<string>();
    }

    /// <summary>
    /// Parameter mapping checks, pure (tested in ProjectPerseusTests/ParamCompatTest.cs): can a consultant parameter feed a local
    /// one (spec-aware: text never silently becomes a number; numbers can always become text), and what each value becomes.
    /// </summary>
    public static class ParamCompat
    {
        public const string Text = "Text", Number = "Number", Measure = "Measure", Integer = "Integer", YesNo = "YesNo", Element = "Element";

        private static bool Numeric(string k) => k == Number || k == Measure || k == Integer || k == YesNo;

        /// <summary>Spec id without its version ("autodesk.spec.aec:length-2.0.0" -> "autodesk.spec.aec:length").</summary>
        public static string SpecKey(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return "";
            int dash = spec.LastIndexOf('-');
            return dash > 0 && dash > spec.LastIndexOf(':') ? spec.Substring(0, dash) : spec;
        }

        /// <summary>
        /// GUID of a parameter Perseus adds for a mapping: from name + data type (spec without version), so every user and model
        /// generates the same definition, and "Fire Rating" as text and as a number are different parameters. (Perseus' own
        /// Perseus_* parameters keep their name-only GUIDs: ids/SharedParameterManager.GuidFor.)
        /// </summary>
        public static Guid GuidFor(string name, string spec)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    "ProjectPerseus.MappedParameter:" + (name ?? "").Trim().ToLowerInvariant() + "|" + SpecKey(spec).ToLowerInvariant())));
        }

        /// <summary>Data type a new local parameter gets for this consultant parameter (spec id): its own, element references as text.</summary>
        public static string SpecForNew(ParamRef src, string textSpec) =>
            src == null || src.Kind == Element || src.Kind == Text || string.IsNullOrEmpty(src.Spec) ? textSpec : src.Spec;

        private static string L(ParamRef p) => p.SpecLabel ?? p.Kind;

        public static Compat Check(ParamRef src, ParamRef dst)
        {
            if (src == null || dst == null) return Compat.Error("Pick both parameters.");
            if (dst.ReadOnly) return Compat.Error($"{dst.Name} is read-only here (formula, reporting or set by Revit).");
            var c = CheckKinds(src, dst);
            if (c.Level != CompatLevel.Error && dst.IsType && !src.IsType && src.Computed == null)
                return Compat.Warn((c.Level == CompatLevel.Warn ? c.Message + " " : "") +
                                   "A type parameter fed from an instance parameter: consultant elements of this type may disagree (the preview counts distinct values).");
            return c;
        }

        private static Compat CheckKinds(ParamRef src, ParamRef dst)
        {
            string s = src.Kind, d = dst.Kind;
            if (d == Text)
                return s == Text ? Compat.Ok("Text to text.")
                     : s == Element ? Compat.Ok("The element's name is written as text.")
                     : s == YesNo ? Compat.Ok("Written as Yes / No.")
                     : s == Measure ? Compat.Ok($"Written as text with its units ({L(src)}).")
                     : Compat.Ok("Number written as text.");
            if (s == Element || d == Element)
                return Compat.Error("Element references (material, level...) can't cross models: map them to a text parameter (the name is written).");
            if (s == Text)
                return d == YesNo ? Compat.Warn("Text is read as Yes / No: only yes / no / true / false / 1 / 0 map, other values fail.")
                     : Compat.Warn($"Text is read as a number{(d == Measure ? $" in project units ({L(dst)})" : "")}: values that aren't numbers fail.");
            if (s == d && s != Measure) return Compat.Ok($"{s} to {d}.");
            if (s == Measure && d == Measure)
                return SpecKey(src.Spec) == SpecKey(dst.Spec) ? Compat.Ok($"{L(src)} to {L(dst)}.")
                     : Compat.Error($"{L(src)} can't map to {L(dst)} (different kinds of value).");
            if (s == Measure && d == YesNo) return Compat.Error($"{L(src)} can't map to Yes / No.");
            if (s == Measure) return Compat.Warn($"The {L(src)} value in project units is written as a plain {(d == Integer ? "integer (rounded)" : "number")}.");
            if (d == Measure)
                return s == YesNo ? Compat.Error($"Yes / No can't map to {L(dst)}.")
                     : Compat.Warn($"The number is taken as {L(dst)} in project units.");
            if (d == YesNo) return Compat.Warn("0 = No, anything else = Yes.");
            if (d == Integer && s == Number) return Compat.Warn("Rounded to a whole number.");
            return Compat.Ok($"{s} to {d}.");   // integer / yes-no -> number
        }

        private static readonly Regex NumberWithUnit = new Regex(@"^\s*([-+]?\d+(?:[.,]\d+)?|[-+]?[.,]\d+)\s*[a-zA-Z%°²³""'/ ]*\s*$", RegexOptions.Compiled);

        public static bool TryParseNumber(string text, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return true;
            var m = NumberWithUnit.Match(text);
            return m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        public static bool TryParseYesNo(string text, out bool v)
        {
            v = false;
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "yes": case "y": case "true": case "1": v = true; return true;
                case "no": case "n": case "false": case "0": return true;
                default: return false;
            }
        }

        private static string N(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

        /// <summary>What one consultant value becomes in the local parameter (shown as text in the preview).</summary>
        public static ConvertedValue Convert(ParamRef src, ParamRef dst, SourceValue v)
        {
            if (v == null || !v.HasValue || (src.Kind == Text && string.IsNullOrEmpty(v.Text)))
                return new ConvertedValue { Ok = true, Empty = true, Shown = "(empty)" };
            if (CheckKinds(src, dst).Level == CompatLevel.Error) return new ConvertedValue { Problem = "doesn't map" };
            string s = src.Kind, d = dst.Kind;
            ConvertedValue Ok(string shown) => new ConvertedValue { Ok = true, Shown = shown };
            ConvertedValue Fail(string why) => new ConvertedValue { Problem = why };

            if (d == Text)
                return Ok(s == YesNo ? (v.Number.GetValueOrDefault() != 0 ? "Yes" : "No") : v.Text ?? (v.Number.HasValue ? N(v.Number.Value) : ""));
            if (s == Text)
            {
                if (d == YesNo) return TryParseYesNo(v.Text, out bool b) ? Ok(b ? "Yes" : "No") : Fail($"'{v.Text}' isn't yes / no");
                if (!TryParseNumber(v.Text, out double n)) return Fail($"'{v.Text}' isn't a number");
                return Ok(d == Integer ? Math.Round(n).ToString(CultureInfo.CurrentCulture) : d == Measure ? $"{N(n)} ({L(dst)}, project units)" : N(n));
            }
            double num = s == Measure ? (v.Display ?? v.Number ?? 0) : v.Number ?? 0;
            if (s == Measure && d == Measure) return Ok(v.Text ?? N(num));
            if (d == YesNo) return Ok(num != 0 ? "Yes" : "No");
            if (d == Integer) return Ok(Math.Round(num).ToString(CultureInfo.CurrentCulture));
            if (d == Measure) return Ok($"{N(num)} ({L(dst)}, project units)");
            return Ok(N(num));
        }

        /// <summary>
        /// The value to write into the local parameter: Text, Int, Internal (Revit internal units, e.g. a length copied from a
        /// length) or Display (a number in the host's display units for dst's spec: converted to internal on the Revit side).
        /// Not Ok = nothing written (doesn't convert, or an empty consultant value into a non-text parameter).
        /// </summary>
        public static TypedValue TargetValue(ParamRef src, ParamRef dst, SourceValue v)
        {
            var c = Convert(src, dst, v);
            var t = new TypedValue { Shown = c.Shown, Empty = c.Empty };
            if (c.Empty) { if (dst.Kind == Text) { t.Ok = true; t.Text = ""; } else t.Problem = "empty"; return t; }
            if (!c.Ok) { t.Problem = c.Problem; return t; }
            string s = src.Kind, d = dst.Kind;
            t.Ok = true;
            if (d == Text) { t.Text = c.Shown; return t; }
            if (s == Text)
            {
                if (d == YesNo) { TryParseYesNo(v.Text, out bool b); t.Int = b ? 1 : 0; return t; }
                TryParseNumber(v.Text, out double n);
                if (d == Integer) t.Int = (int)Math.Round(n); else if (d == Measure) t.Display = n; else t.Internal = n;
                return t;
            }
            double num = s == Measure ? (v.Display ?? v.Number ?? 0) : v.Number ?? 0;
            if (d == YesNo) t.Int = num != 0 ? 1 : 0;
            else if (d == Integer) t.Int = (int)Math.Round(num);
            else if (d == Measure) { if (s == Measure) t.Internal = v.Number; else t.Display = num; }
            else t.Internal = num;   // Number
            return t;
        }

        /// <summary>Canonical text of a consultant value (baselines / rejections): the raw number (internal) or the text.</summary>
        public static string SourceCanon(SourceValue v) =>
            v == null || !v.HasValue ? "empty" : v.Number.HasValue ? "n:" + Math.Round(v.Number.Value, 6).ToString("R", CultureInfo.InvariantCulture) : "t:" + (v.Text ?? "");

        /// <summary>Canonical text of a local parameter value (what's written / what's there): by storage.</summary>
        public static string LocalCanon(string storage, string text, int? i, double? internalValue) =>
            storage == "String" ? "t:" + (text ?? "") : storage == "Integer" ? "i:" + i.GetValueOrDefault()
            : "d:" + Math.Round(internalValue.GetValueOrDefault(), 6).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The mapping applied to the consultant values: counts, the first samples, the first problems.</summary>
        public static ParamPreview Preview(ParamRef src, ParamRef dst, IList<SourceValue> values, int samples = 6)
        {
            var p = new ParamPreview();
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in values ?? new List<SourceValue>())
            {
                p.Total++;
                var c = Convert(src, dst, v);
                if (c.Empty) p.Empty++;
                else if (c.Ok) p.Converted++;
                else { p.Failing++; if (p.Problems.Count < 4 && !p.Problems.Contains(c.Problem)) p.Problems.Add(c.Problem); }
                distinct.Add(v?.HasValue == true ? v.Text ?? "" : "\0");
                if (p.Samples.Count < samples && !c.Empty && !p.Samples.Any(x => x.Item1 == (v.Text ?? "")))
                    p.Samples.Add(Tuple.Create(v.Text ?? "", c.Ok ? c.Shown : "✗ " + c.Problem));
            }
            p.Distinct = distinct.Count;
            return p;
        }
    }
}
