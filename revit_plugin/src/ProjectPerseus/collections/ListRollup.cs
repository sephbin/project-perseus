using System;
using System.Collections.Generic;
using System.Linq;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Pure: how a collection list (models/CollectionListDto) turns the members' values into the owner's text. Values in
    /// slot order; empty ones dropped (SkipEmpty, the default) or kept as empty entries; Unique keeps the first of each
    /// value (case-sensitive, after trimming). Tested in ReferenceCodecTest.cs.
    /// </summary>
    public static class ListRollup
    {
        /// <summary>
        /// The delimiter as stored, with the escapes a web form may send: \n (new line), \t (tab), \\ (backslash).
        /// Null = ", "; an empty string joins with nothing.
        /// </summary>
        public static string Delimiter(string stored)
        {
            if (stored == null) return ", ";
            if (stored.IndexOf('\\') < 0) return stored;
            var sb = new System.Text.StringBuilder(stored.Length);
            for (int i = 0; i < stored.Length; i++)
            {
                char c = stored[i];
                if (c == '\\' && i + 1 < stored.Length)
                {
                    char n = stored[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The owner's text for these member values (slot order).</summary>
        public static string Join(IEnumerable<string> values, string delimiter, bool skipEmpty, bool unique)
        {
            var items = (values ?? Enumerable.Empty<string>()).Select(v => (v ?? "").Trim());
            if (skipEmpty) items = items.Where(v => v.Length > 0);
            if (unique) items = items.Distinct(StringComparer.Ordinal);
            return string.Join(Delimiter(delimiter), items);
        }
    }
}
