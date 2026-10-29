using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Parsed ID pattern such as "{Level.Name}-{CategoryPrefix}-{Increment|4}". Pure logic, no Revit dependencies.
    /// Tokens:
    ///   {Increment|N}              exactly one, N = 1..9 minimum digits
    ///   {CategoryPrefix}           prefix configured for the element's category
    ///   {Workset}                  name of the element's workset
    ///   {Level|Host|Room|Space|Type|Group.ParamName}   parameter of a related element ("Name" = its name)
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
            @"^(Level|Host|Room|Space|Type|Group)\.[A-Za-z0-9 _\-]{1,64}$", RegexOptions.CultureInvariant);

        private readonly List<Segment> _segments;
        private readonly Dictionary<string, Regex> _regexByScope = new Dictionary<string, Regex>();

        /// <summary>Distinct non-counter tokens in order of first appearance, e.g. "Level.Name", "CategoryPrefix".</summary>
        public IReadOnlyList<string> Tokens { get; }
        public bool UsesCategoryPrefix { get; }
        public int Width { get; }

        private IdPattern(List<Segment> segments, int width)
        {
            _segments = segments;
            Width = width;
            Tokens = segments.Where(s => s.Kind == Kind.Token).Select(s => s.Text).Distinct().ToList();
            UsesCategoryPrefix = Tokens.Contains("CategoryPrefix");
        }

        /// <summary>Returns null when the pattern is invalid (unknown token, unmatched brace, or not exactly one Increment).</summary>
        public static IdPattern Parse(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

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

                if (token == "CategoryPrefix" || token == "Workset" || RelatedToken.IsMatch(token))
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

            return increments == 1 ? new IdPattern(segments, width) : null;
        }

        /// <summary>
        /// Identifies the counter this element belongs to: the resolved token values joined together.
        /// Elements with the same scope share one sequence (e.g. all walls on Level 02).
        /// Returns null when any token cannot be resolved.
        /// </summary>
        public string ScopeKey(Func<string, string> resolve)
        {
            var parts = new List<string>(Tokens.Count);
            foreach (var t in Tokens)
            {
                string v = resolve(t);
                if (string.IsNullOrEmpty(v)) return null;
                parts.Add(v);
            }
            return string.Join("\u001f", parts);
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
            if (string.IsNullOrEmpty(value)) return false;

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
