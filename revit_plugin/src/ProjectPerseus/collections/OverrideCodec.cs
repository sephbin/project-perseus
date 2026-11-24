using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Pure: an owner instance's overrides of its collection members' values, stored as JSON in the collection's override
    /// parameter, keyed by slot (a member can sit in two slots): {"1": {"Message": "Plant Room 3"}, "3": {"Arrow": "Left"}}.
    /// Field names compare case-insensitively (keeping the spelling first written). Values are text ("" is a real override:
    /// blank this field). Tested in ReferenceCodecTest.cs.
    /// </summary>
    public class Overrides
    {
        private readonly SortedDictionary<int, Dictionary<string, string>> _slots =
            new SortedDictionary<int, Dictionary<string, string>>();

        public IEnumerable<int> Slots => _slots.Keys;
        public bool IsEmpty => _slots.Count == 0;

        /// <summary>Overrides of one slot (empty when none); a copy.</summary>
        public Dictionary<string, string> For(int slot) =>
            _slots.TryGetValue(slot, out var f)
                ? new Dictionary<string, string>(f, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(int slot, string field, out string value)
        {
            value = null;
            return _slots.TryGetValue(slot, out var f) && f.TryGetValue(field ?? "", out value);
        }

        public void Set(int slot, string field, string value)
        {
            if (slot < 1 || string.IsNullOrWhiteSpace(field)) return;
            if (!_slots.TryGetValue(slot, out var f))
                _slots[slot] = f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            f[field.Trim()] = value ?? "";
        }

        public bool Clear(int slot, string field)
        {
            if (!_slots.TryGetValue(slot, out var f) || !f.Remove(field ?? "")) return false;
            if (f.Count == 0) _slots.Remove(slot);
            return true;
        }

        public bool ClearSlot(int slot) => _slots.Remove(slot);

        /// <summary>Moves overrides with their members when slots are reordered / removed: map old slot -> new slot;
        /// slots missing from the map (removed members) lose their overrides. Returns how many slots were dropped.</summary>
        public int Remap(IDictionary<int, int> oldToNew)
        {
            var copy = _slots.ToList();
            _slots.Clear();
            int dropped = 0;
            foreach (var kv in copy)
            {
                if (oldToNew != null && oldToNew.TryGetValue(kv.Key, out int to) && to >= 1) _slots[to] = kv.Value;
                else dropped++;
            }
            return dropped;
        }

        /// <summary>Drops overrides of slots past the last member (e.g. members removed from the type).</summary>
        public int PruneAbove(int slotCount)
        {
            var gone = _slots.Keys.Where(s => s > slotCount).ToList();
            foreach (var s in gone) _slots.Remove(s);
            return gone.Count;
        }

        /// <summary>Member values with this slot's overrides on top (what templates see).</summary>
        public Dictionary<string, string> Merge(int slot, IDictionary<string, string> baseValues)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (baseValues != null) foreach (var kv in baseValues) result[kv.Key] = kv.Value;
            if (_slots.TryGetValue(slot, out var f)) foreach (var kv in f) result[kv.Key] = kv.Value;
            return result;
        }

        public Overrides Clone()
        {
            var o = new Overrides();
            foreach (var kv in _slots)
                o._slots[kv.Key] = new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase);
            return o;
        }
    }

    public static class OverrideCodec
    {
        /// <summary>Parses an override parameter value. error = why (part of) it couldn't be read; readable slots are kept.</summary>
        public static Overrides Parse(string text, out string error)
        {
            error = null;
            var result = new Overrides();
            if (string.IsNullOrWhiteSpace(text)) return result;
            JToken root;
            try { root = JToken.Parse(text); }
            catch (JsonException ex)
            {
                error = "not valid JSON (" + ex.Message + ")";
                return result;
            }
            if (!(root is JObject obj))
            {
                error = "not a JSON object of slot -> fields";
                return result;
            }
            foreach (var prop in obj.Properties())
            {
                if (!int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) || slot < 1)
                {
                    error = $"'{prop.Name}' is not a slot number";
                    continue;
                }
                if (!(prop.Value is JObject fields))
                {
                    error = $"slot {slot} is not an object of field -> value";
                    continue;
                }
                foreach (var f in fields.Properties())
                    result.Set(slot, f.Name, ValueText(f.Value));
            }
            return result;
        }

        private static string ValueText(JToken v)
        {
            switch (v.Type)
            {
                case JTokenType.Null: return "";
                case JTokenType.String: return (string)v;
                case JTokenType.Boolean: return (bool)v ? "Yes" : "No";
                case JTokenType.Integer:
                case JTokenType.Float: return Convert.ToString(((JValue)v).Value, CultureInfo.InvariantCulture);
                default: return v.ToString(Formatting.None);
            }
        }

        /// <summary>Compact JSON, slots in order; "" when there are no overrides (the parameter is then cleared).</summary>
        public static string Serialize(Overrides o)
        {
            if (o == null || o.IsEmpty) return "";
            var root = new JObject();
            foreach (int slot in o.Slots)
            {
                var fields = new JObject();
                foreach (var kv in o.For(slot).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) fields[kv.Key] = kv.Value;
                root[slot.ToString(CultureInfo.InvariantCulture)] = fields;
            }
            return root.ToString(Formatting.None);
        }
    }
}
