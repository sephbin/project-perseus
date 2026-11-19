using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Parsed Auto Parameters pattern such as "{Level.Name}-{CategoryPrefix}-{Increment|4}". Pure logic, no Revit dependencies.
    /// Tokens:
    ///   {Increment|N}              at most one, N = 1..9 minimum digits. ID rules need exactly one (HasCounter);
    ///                              value rules have none (see ForRule)
    ///   {CategoryPrefix}           prefix configured for the element's category
    ///   {Workset}                  name of the element's workset
    ///   {Level|Host|Room|Space|ToRoom|FromRoom|Type|Group|Project|Element.ParamName}   parameter of a related
    ///                              element ("Name" = its name; Project = the model's Project Information; ToRoom /
    ///                              FromRoom = a door's or window's rooms; Element = the element's own parameter)
    ///   {A|B|...}                  fallbacks: the first of the tokens that has a value, e.g. {ToRoom.Number|FromRoom.Number}
    /// Handlebars-style {{Level.Name}} (and padded { Level.Name }) is accepted and read as {Level.Name} (Normalise).
    /// Anything else is literal. Token values are supplied by a resolver so this class stays testable.
    /// A resolver returns null/empty when a value is unavailable; callers must then skip the element.
    /// </summary>
    public sealed class IdPattern
    {
        private enum Kind { Literal, Token, Increment }

        private struct Segment
        {
            public Kind Kind;
            public string Text;
        }

        private static readonly Regex RelatedToken = new Regex(
            @"^(Level|Host|Room|Space|ToRoom|FromRoom|Type|Group|Project|Element)\.[A-Za-z0-9 _\-]{1,64}$", RegexOptions.CultureInvariant);

        private readonly List<Segment> _segments;
        private readonly Dictionary<string, Regex> _regexByScope = new Dictionary<string, Regex>();

        /// <summary>Distinct non-counter tokens in order of first appearance, e.g. "Level.Name", "CategoryPrefix".</summary>
        public IReadOnlyList<string> Tokens { get; }
        /// <summary>
        /// ID rules: extra tokens the value only has to be unique with ("Unique with"), not printed in it. Doors
        /// numbered D01, D02... per room: pattern "D{Increment|2}", unique with "{Element.Door Tag Room Number}".
        /// Part of the counter scope, and of <see cref="UniqueKey"/>.
        /// </summary>
        public IReadOnlyList<string> UniqueWithTokens { get; private set; } = new List<string>();
        public bool HasUniqueWith => UniqueWithTokens.Count > 0;
        /// <summary>Every token the element's values depend on: printed ones, then unique-with ones.</summary>
        public IEnumerable<string> AllTokens => Tokens.Concat(UniqueWithTokens);
        public bool UsesCategoryPrefix { get; }
        public int Width { get; }
        /// <summary>True when the pattern has its {Increment|N} counter (ID rules); false for value patterns.</summary>
        public bool HasCounter => Width > 0;

        private IdPattern(List<Segment> segments, int width)
        {
            _segments = segments;
            Width = width;
            Tokens = segments.Where(s => s.Kind == Kind.Token).Select(s => s.Text).Distinct().ToList();
            UsesCategoryPrefix = Tokens.Contains("CategoryPrefix");
        }

        /// <summary>
        /// Parses a rule's pattern for its kind: ID rules need exactly one {Increment|N}, value rules none.
        /// Null when invalid or the wrong shape for the kind.
        /// </summary>
        public static IdPattern ForRule(string pattern, bool isValue, string uniqueWith = null)
        {
            var p = Parse(pattern);
            if (p == null || p.HasCounter == isValue) return null;
            if (isValue || string.IsNullOrWhiteSpace(uniqueWith)) return p;
            var u = ParseUniqueWith(uniqueWith);
            if (u == null) return null;
            p.UniqueWithTokens = u.Where(t => !p.Tokens.Contains(t)).ToList();
            return p;
        }

        /// <summary>
        /// Tokens of a "Unique with" field such as "{Element.Door Tag Room Number}" or "{ToRoom.Number|FromRoom.Number}"
        /// (literal text between tokens is ignored). Empty list for a blank field; null when invalid.
        /// </summary>
        public static List<string> ParseUniqueWith(string uniqueWith)
        {
            if (string.IsNullOrWhiteSpace(uniqueWith)) return new List<string>();
            var p = Parse(uniqueWith);
            return p == null || p.HasCounter || p.Tokens.Count == 0 ? null : p.Tokens.ToList();
        }

        private static bool IsToken(string token)
        {
            if (token == "CategoryPrefix" || token == "Workset" || RelatedToken.IsMatch(token)) return true;
            // Fallbacks {A|B}: every alternative must be a token itself.
            if (token.IndexOf('|') < 0 || token.StartsWith("Increment|", StringComparison.Ordinal)) return false;
            var parts = token.Split('|');
            return parts.All(a => a == "CategoryPrefix" || a == "Workset" || RelatedToken.IsMatch(a));
        }

        /// <summary>The alternatives of a token: "ToRoom.Number|FromRoom.Number" -> both, "Level.Name" -> itself.</summary>
        public static string[] Alternatives(string token) => token.Split('|');

        private static readonly Regex DoubleBraces = new Regex(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.CultureInvariant);
        private static readonly Regex PaddedBraces = new Regex(@"\{\s*([^{}]*?)\s*\}", RegexOptions.CultureInvariant);

        /// <summary>
        /// Handlebars-style spelling to Perseus tokens: "{{ Level.Name }}" -> "{Level.Name}", "{ Room.Number }" ->
        /// "{Room.Number}". Templates typed into a type parameter are often written that way.
        /// </summary>
        public static string Normalise(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('{') < 0) return pattern;
            return PaddedBraces.Replace(DoubleBraces.Replace(pattern, "{$1}"), "{$1}");
        }

        /// <summary>
        /// The token of a value rule's "template from" field: "Type.Tag Template", "{Type.Tag Template}" or
        /// "{{Type.Tag Template}}" -> "Type.Tag Template". Null when it isn't a parameter token.
        /// </summary>
        public static string ParseSource(string source)
        {
            string s = (source ?? "").Trim();
            if (s.Length == 0) return null;
            s = Normalise(s);
            if (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal))
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.IndexOf('{') >= 0 || s.IndexOf('}') >= 0) return null;
            return IdPattern.Alternatives(s).All(a => RelatedToken.IsMatch(a)) ? s : null;
        }

        /// <summary>Returns null when the pattern is invalid (unknown token, unmatched brace, or more than one Increment).</summary>
        public static IdPattern Parse(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            pattern = Normalise(pattern);

            var segments = new List<Segment>();
            var literal = new StringBuilder();
            int increments = 0;
            int width = 0;

            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c == '}') return null;
                if (c != '{') { literal.Append(c); i++; continue; }

                int close = pattern.IndexOf('}', i + 1);
                if (close < 0) return null;
                string token = pattern.Substring(i + 1, close - i - 1);

                if (literal.Length > 0)
                {
                    segments.Add(new Segment { Kind = Kind.Literal, Text = literal.ToString() });
                    literal.Clear();
                }

                if (IsToken(token))
                {
                    segments.Add(new Segment { Kind = Kind.Token, Text = token });
                }
                else if (token.StartsWith("Increment|", StringComparison.Ordinal) &&
                         int.TryParse(token.Substring("Increment|".Length), NumberStyles.None,
                                      CultureInfo.InvariantCulture, out int w) && w >= 1 && w <= 9)
                {
                    segments.Add(new Segment { Kind = Kind.Increment });
                    width = w;
                    increments++;
                }
                else
                {
                    return null;
                }
                i = close + 1;
            }

            if (literal.Length > 0)
                segments.Add(new Segment { Kind = Kind.Literal, Text = literal.ToString() });

            return increments <= 1 ? new IdPattern(segments, width) : null;
        }

        /// <summary>
        /// Identifies the counter this element belongs to: the resolved token values joined together.
        /// Elements with the same scope share one sequence (e.g. all walls on Level 02).
        /// Returns null when any token cannot be resolved.
        /// </summary>
        public string ScopeKey(Func<string, string> resolve)
        {
            var parts = new List<string>(Tokens.Count + UniqueWithTokens.Count);
            foreach (var t in AllTokens)
            {
                string v = resolve(t);
                if (string.IsNullOrEmpty(v)) return null;
                parts.Add(v);
            }
            return string.Join("\u001f", parts);
        }

        /// <summary>
        /// What must be unique for an ID rule: the value itself, or with "Unique with" tokens the value plus their
        /// values, e.g. "D01 [101]" (so D01 can repeat in other rooms). Used by the index and the server claims.
        /// </summary>
        public string UniqueKey(string value, Func<string, string> resolve)
        {
            if (string.IsNullOrEmpty(value) || !HasUniqueWith) return value;
            return value + " [" + string.Join(" / ", UniqueWithTokens.Select(t => resolve(t) ?? "")) + "]";
        }

        public string Render(Func<string, string> resolve, long number)
        {
            var sb = new StringBuilder();
            foreach (var s in _segments)
            {
                switch (s.Kind)
                {
                    case Kind.Literal: sb.Append(s.Text); break;
                    case Kind.Token: sb.Append(resolve(s.Text) ?? ""); break;
                    case Kind.Increment:
                        sb.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0'));
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Convenience for patterns that only use {CategoryPrefix}.</summary>
        public string Render(string categoryPrefix, long number) =>
            Render(t => t == "CategoryPrefix" ? (categoryPrefix ?? "") : "", number);

        /// <summary>
        /// True when <paramref name="value"/> was produced by this pattern for the element whose token values
        /// <paramref name="resolve"/> returns; <paramref name="number"/> is the counter portion.
        /// </summary>
        public bool TryExtractNumber(string value, Func<string, string> resolve, out long number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value) || !HasCounter) return false;

            var values = Tokens.ToDictionary(t => t, t => resolve(t) ?? "");
            string scope = string.Join("\u001f", Tokens.Select(t => values[t]));

            Regex re;
            lock (_regexByScope)
            {
                if (!_regexByScope.TryGetValue(scope, out re))
                {
                    var sb = new StringBuilder("^");
                    foreach (var s in _segments)
                    {
                        switch (s.Kind)
                        {
                            case Kind.Literal: sb.Append(Regex.Escape(s.Text)); break;
                            case Kind.Token: sb.Append(Regex.Escape(values[s.Text])); break;
                            case Kind.Increment: sb.Append("(\\d{").Append(Width).Append(",})"); break;
                        }
                    }
                    sb.Append('$');
                    re = new Regex(sb.ToString(), RegexOptions.CultureInvariant);
                    _regexByScope[scope] = re;
                }
            }

            var m = re.Match(value);
            return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>Convenience for patterns that only use {CategoryPrefix}.</summary>
        public bool TryExtractNumber(string value, string categoryPrefix, out long number) =>
            TryExtractNumber(value, t => t == "CategoryPrefix" ? (categoryPrefix ?? "") : "", out number);
    }
}
