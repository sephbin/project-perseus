using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Parsed ID pattern such as "{CategoryPrefix}{Increment|4}". Pure logic, no Revit dependencies.
    /// Tokens: {CategoryPrefix}, {Increment|N} (exactly one, N = 1..9 minimum digits). Anything else is literal.
    /// </summary>
    public sealed class IdPattern
    {
        private enum Kind { Literal, CategoryPrefix, Increment }

        private struct Segment
        {
            public Kind Kind;
            public string Text;
        }

        private readonly List<Segment> _segments;
        private readonly Dictionary<string, Regex> _regexByPrefix = new Dictionary<string, Regex>();

        public bool UsesCategoryPrefix { get; }
        public int Width { get; }

        private IdPattern(List<Segment> segments, bool usesPrefix, int width)
        {
            _segments = segments;
            UsesCategoryPrefix = usesPrefix;
            Width = width;
        }

        /// <summary>Returns null when the pattern is invalid (unknown token, unmatched brace, or not exactly one Increment).</summary>
        public static IdPattern Parse(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            var segments = new List<Segment>();
            var literal = new StringBuilder();
            bool usesPrefix = false;
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

                if (token == "CategoryPrefix")
                {
                    segments.Add(new Segment { Kind = Kind.CategoryPrefix });
                    usesPrefix = true;
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

            return increments == 1 ? new IdPattern(segments, usesPrefix, width) : null;
        }

        public string Render(string categoryPrefix, long number)
        {
            var sb = new StringBuilder();
            foreach (var s in _segments)
            {
                switch (s.Kind)
                {
                    case Kind.Literal: sb.Append(s.Text); break;
                    case Kind.CategoryPrefix: sb.Append(categoryPrefix ?? ""); break;
                    case Kind.Increment:
                        sb.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(Width, '0'));
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// True when <paramref name="value"/> was produced by this pattern for the given prefix;
        /// <paramref name="number"/> is the counter portion.
        /// </summary>
        public bool TryExtractNumber(string value, string categoryPrefix, out long number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value)) return false;

            string key = UsesCategoryPrefix ? (categoryPrefix ?? "") : "";
            Regex re;
            lock (_regexByPrefix)
            {
                if (!_regexByPrefix.TryGetValue(key, out re))
                {
                    var sb = new StringBuilder("^");
                    foreach (var s in _segments)
                    {
                        switch (s.Kind)
                        {
                            case Kind.Literal: sb.Append(Regex.Escape(s.Text)); break;
                            case Kind.CategoryPrefix: sb.Append(Regex.Escape(key)); break;
                            case Kind.Increment: sb.Append("(\\d{").Append(Width).Append(",})"); break;
                        }
                    }
                    sb.Append('$');
                    re = new Regex(sb.ToString(), RegexOptions.CultureInvariant);
                    _regexByPrefix[key] = re;
                }
            }

            var m = re.Match(value);
            return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }
    }
}
