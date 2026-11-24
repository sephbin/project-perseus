using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectPerseus.collections
{
    /// <summary>A stored reference to a collection member: ElementId + element name (neither trusted alone). Id 0 with a
    /// name = a name-only reference (e.g. a hand-typed "ST07" from before collections), resolved by name.</summary>
    public struct MemberRef : IEquatable<MemberRef>
    {
        public long Id;
        public string Name;

        public MemberRef(long id, string name)
        {
            Id = id;
            Name = name ?? "";
        }

        public static readonly MemberRef Empty = new MemberRef(0, "");
        public bool IsEmpty => Id <= 0 && string.IsNullOrEmpty(Name);

        public bool Equals(MemberRef o) => Id == o.Id && string.Equals(Name ?? "", o.Name ?? "", StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is MemberRef o && Equals(o);
        public override int GetHashCode() => Id.GetHashCode() ^ (Name ?? "").GetHashCode();
        public override string ToString() => IsEmpty ? "(empty)" : $"{Id}|{Name}";
    }

    /// <summary>
    /// Pure text formats of reference collections (REFERENCE_COLLECTIONS_PLAN.md, documented for Grasshopper in the
    /// django-perseus help guide "collections"):
    ///   slot parameter  "12345|ST07 Exit"   (ElementId | name; '\' and '|' in the name escaped as "\\" and "\|"; "" = empty)
    ///   list parameter  [{"id":12345,"name":"ST07 Exit"}, null, ...]   (null = empty slot, order = slot order)
    ///   slot names      pattern "COX_Sign_{n}" -> COX_Sign_1, COX_Sign_2 ...
    /// Slots are 1-based everywhere (overrides are keyed by the same numbers). Tested in ReferenceCodecTest.cs.
    /// </summary>
    public static class ReferenceCodec
    {
        // ── slot text ────────────────────────────────────────────────────────

        public static string EncodeSlot(MemberRef r)
        {
            if (r.IsEmpty) return "";
            return (r.Id > 0 ? r.Id.ToString(CultureInfo.InvariantCulture) : "") + "|" + Escape(r.Name);
        }

        /// <summary>Parses a slot value. Text without '|' (or with a non-numeric id part) is a name-only reference.</summary>
        public static MemberRef DecodeSlot(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return MemberRef.Empty;
            int bar = text.IndexOf('|');
            if (bar < 0) return new MemberRef(0, text.Trim());
            string idPart = text.Substring(0, bar).Trim();
            string name = Unescape(text.Substring(bar + 1));
            if (idPart.Length == 0) return new MemberRef(0, name);
            if (long.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0)
                return new MemberRef(id, name);
            return new MemberRef(0, Unescape(text).Trim()); // not ours: keep the whole text as a name
        }

        private static string Escape(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("\\", "\\\\").Replace("|", "\\|");
        }

        private static string Unescape(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('\\') < 0) return text ?? "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\\' || text[i + 1] == '|'))
                {
                    sb.Append(text[i + 1]);
                    i++;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ── list text ────────────────────────────────────────────────────────

        /// <summary>JSON array, null for empty slots; trailing empty slots dropped; "" when there are no members.</summary>
        public static string EncodeList(IList<MemberRef> members)
        {
            var trimmed = TrimTrailingEmpty(members);
            if (trimmed.Count == 0) return "";
            var arr = new JArray();
            foreach (var m in trimmed)
                arr.Add(m.IsEmpty ? (JToken)JValue.CreateNull() : new JObject { ["id"] = m.Id, ["name"] = m.Name ?? "" });
            return arr.ToString(Formatting.None);
        }

        /// <summary>Parses a list value; error = why it couldn't be read (the list is then empty). Accepts bare strings
        /// in the array as slot texts ("12345|Name" or a name).</summary>
        public static List<MemberRef> DecodeList(string text, out string error)
        {
            error = null;
            var result = new List<MemberRef>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException ex)
            {
                error = "not valid JSON (" + ex.Message + ")";
                return result;
            }
            if (!(root is JArray arr))
            {
                error = "not a JSON list";
                return result;
            }
            foreach (var item in arr)
            {
                switch (item.Type)
                {
                    case JTokenType.Null:
                        result.Add(MemberRef.Empty);
                        break;
                    case JTokenType.String:
                        result.Add(DecodeSlot((string)item));
                        break;
                    case JTokenType.Object:
                        long id = 0;
                        var idTok = item["id"];
                        if (idTok != null && (idTok.Type == JTokenType.Integer || idTok.Type == JTokenType.String))
                            long.TryParse(idTok.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                        result.Add(new MemberRef(Math.Max(0, id), (string)item["name"] ?? ""));
                        break;
                    default:
                        error = $"slot {result.Count + 1} is not a reference";
                        result.Add(MemberRef.Empty);
                        break;
                }
            }
            return result;
        }

        public static List<MemberRef> TrimTrailingEmpty(IEnumerable<MemberRef> members)
        {
            var list = (members ?? Enumerable.Empty<MemberRef>()).ToList();
            while (list.Count > 0 && list[list.Count - 1].IsEmpty) list.RemoveAt(list.Count - 1);
            return list;
        }

        // ── slot parameter names ─────────────────────────────────────────────

        public static string SlotName(string pattern, int slot) =>
            (pattern ?? "").Replace(models.CollectionDto.SlotToken, slot.ToString(CultureInfo.InvariantCulture));

        /// <summary>Slot number of a parameter name made from the pattern (case-insensitive), else 0.</summary>
        public static int SlotNumber(string pattern, string name)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name)) return 0;
            int at = pattern.IndexOf(models.CollectionDto.SlotToken, StringComparison.Ordinal);
            if (at < 0) return 0;
            string before = pattern.Substring(0, at), after = pattern.Substring(at + models.CollectionDto.SlotToken.Length);
            var m = Regex.Match(name, "^" + Regex.Escape(before) + "([0-9]{1,4})" + Regex.Escape(after) + "$", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[1].Value.StartsWith("0") ? 0 : n; // Sign_01 isn't slot 1 (SlotName never writes it)
        }
    }
}
