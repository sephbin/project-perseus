using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.naming;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;
using StorageType = Autodesk.Revit.DB.StorageType;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Revit side of reference collections (REFERENCE_COLLECTIONS_PLAN.md part B): which elements own / can be members of a
    /// collection, the names its parameters have in this model, reading and writing members (ReferenceCodec) and instance
    /// overrides (OverrideCodec), and creating the locked parameters on demand. Revit API context; Write* inside the
    /// caller's transaction, EnsureParameters outside one.
    ///
    /// Parameter names: the server resolves them with the naming convention's prefix (CollectionDto.*ParameterName). The
    /// name a model actually uses is recorded when Perseus creates the parameter (naming/ParameterNameRecords, key
    /// "collection.&lt;name&gt;.members:&lt;base name&gt;"; slots record the pattern), so a later prefix change doesn't strand
    /// existing values, while renaming the base name in the config does switch parameters (like an Auto Parameters rule).
    /// </summary>
    internal static class CollectionStore
    {
        // ── names in use ─────────────────────────────────────────────────────

        internal static string MembersKey(CollectionDto c) => $"collection.{c.Name}.members:{c.MembersParameter}";
        internal static string OverridesKey(CollectionDto c) => $"collection.{c.Name}.overrides:{c.OverrideParameter}";

        /// <summary>Members parameter name in this model (slots: the pattern with {n}).</summary>
        internal static string MembersName(Document doc, CollectionDto c) =>
            ParameterNames.Recorded(doc, MembersKey(c)) ?? Resolved(doc, c.MembersParameterName, c.MembersParameter);

        /// <summary>Override parameter name in this model, or null when the collection has no overrides.</summary>
        internal static string OverrideName(Document doc, CollectionDto c) =>
            !c.HasOverrides ? null : ParameterNames.Recorded(doc, OverridesKey(c)) ?? Resolved(doc, c.OverrideParameterName, c.OverrideParameter);

        private static string Resolved(Document doc, string fromServer, string baseName) =>
            !string.IsNullOrWhiteSpace(fromServer) ? fromServer.Trim() : ParameterNames.WithPrefix(doc, (baseName ?? "").Trim());

        // ── owners and targets ───────────────────────────────────────────────

        private static List<ElementId> CategoryIds(Document doc, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(names ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var ids = new List<ElementId>();
            foreach (Category cat in doc.Settings.Categories)
                if (wanted.Contains(cat.Name)) ids.Add(cat.Id);
            return ids;
        }

        private static FilteredElementCollector Collect(Document doc, IList<string> categories, bool types)
        {
            var col = new FilteredElementCollector(doc);
            col = types ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();
            if (categories != null && categories.Count > 0)
            {
                var ids = CategoryIds(doc, categories);
                if (ids.Count == 0) return null; // none of the categories exists in this model
                col = col.WherePasses(new ElementMulticategoryFilter(ids));
            }
            return col;
        }

        internal static string FamilyName(Element e)
        {
            if (e is ElementType t) return t.FamilyName ?? "";
            var typeId = e?.GetTypeId();
            return typeId != null && typeId != ElementId.InvalidElementId && e.Document.GetElement(typeId) is ElementType et ? et.FamilyName ?? "" : "";
        }

        private static Regex FamilyRegex(CollectionDto c)
        {
            if (string.IsNullOrWhiteSpace(c.OwnerFamily)) return null;
            try { return new Regex(c.OwnerFamily, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException ex)
            {
                Log.Warn($"[CollectionStore] {c.Name}: owner family pattern '{c.OwnerFamily}' is not valid in .NET ({ex.Message}); no owners.");
                return new Regex("(?!)");
            }
        }

        private static bool InCategories(Element e, IList<string> categories) =>
            categories == null || categories.Count == 0 ||
            (e?.Category != null && categories.Any(n => string.Equals(n, e.Category.Name, StringComparison.OrdinalIgnoreCase)));

        internal static bool IsOwner(Element e, CollectionDto c)
        {
            if (e == null || (e is ElementType) != c.OnType || !InCategories(e, c.OwnerCategories)) return false;
            var rx = FamilyRegex(c);
            return rx == null || rx.IsMatch(FamilyName(e));
        }

        internal static List<Element> Owners(Document doc, CollectionDto c)
        {
            var col = Collect(doc, c.OwnerCategories, c.OnType);
            if (col == null) return new List<Element>();
            var rx = FamilyRegex(c);
            return col.Where(e => e.Category != null && (rx == null || rx.IsMatch(FamilyName(e)))).ToList();
        }

        internal static bool IsTarget(Element e, CollectionDto c) =>
            e != null && (e is ElementType) == c.TargetsTypes && e.Category != null && InCategories(e, c.TargetCategories);

        internal static List<Element> Targets(Document doc, CollectionDto c)
        {
            var col = Collect(doc, c.TargetCategories, c.TargetsTypes);
            return col == null ? new List<Element>() : col.Where(e => e.Category != null).ToList();
        }

        internal static string NameOf(Element e) => e?.Name ?? "";

        internal static MemberRef RefTo(Element e) => e == null ? MemberRef.Empty : new MemberRef(e.Id.GetIdValue(), NameOf(e));

        /// <summary>The allowed members of a collection by id and name, for ReferenceResolver.</summary>
        internal class TargetIndex
        {
            private readonly Document _doc;
            private readonly CollectionDto _c;
            private readonly Dictionary<long, string> _byId = new Dictionary<long, string>();
            private readonly Dictionary<string, List<long>> _byName = new Dictionary<string, List<long>>(StringComparer.Ordinal);

            internal TargetIndex(Document doc, CollectionDto c)
            {
                _doc = doc;
                _c = c;
                foreach (var e in Targets(doc, c))
                {
                    long id = e.Id.GetIdValue();
                    string n = NameOf(e);
                    _byId[id] = n;
                    if (!_byName.TryGetValue(n, out var l)) _byName[n] = l = new List<long>();
                    l.Add(id);
                }
            }

            internal int Count => _byId.Count;

            internal TargetInfo Info(long id)
            {
                if (_byId.TryGetValue(id, out var n)) return new TargetInfo(true, true, n);
                var el = id > 0 ? _doc.GetElement(RevitExtensions.CreateId(id)) : null;
                return el == null ? new TargetInfo(false, false, "") : new TargetInfo(true, false, NameOf(el));
            }

            internal IReadOnlyList<long> ByName(string name) =>
                name != null && _byName.TryGetValue(name, out var l) ? (IReadOnlyList<long>)l : Array.Empty<long>();

            internal Resolution Resolve(MemberRef r) => ReferenceResolver.Resolve(r, Info, ByName);
        }

        // ── members ──────────────────────────────────────────────────────────

        /// <summary>Slot number -> parameter of the owner, for every slot parameter it has (bound or not filled).</summary>
        internal static SortedDictionary<int, Parameter> SlotParameters(Element owner, string pattern)
        {
            var result = new SortedDictionary<int, Parameter>();
            foreach (Parameter p in owner.Parameters)
            {
                int n = ReferenceCodec.SlotNumber(pattern, p.Definition?.Name);
                if (n > 0 && p.StorageType == StorageType.String && !result.ContainsKey(n)) result[n] = p;
            }
            return result;
        }

        /// <summary>The owner's members in slot order (index 0 = slot 1; empty slots kept, trailing empties dropped).
        /// error = why the stored value couldn't be read (missing parameter, bad JSON).</summary>
        internal static List<MemberRef> ReadMembers(Element owner, CollectionDto c, string membersName, out string error)
        {
            error = null;
            var result = new List<MemberRef>();
            if (owner == null) return result;
            if (c.UsesSlots)
            {
                foreach (var kv in SlotParameters(owner, membersName))
                {
                    while (result.Count < kv.Key - 1) result.Add(MemberRef.Empty);
                    result.Add(ReferenceCodec.DecodeSlot(kv.Value.AsString()));
                }
                return ReferenceCodec.TrimTrailingEmpty(result);
            }
            var p = owner.LookupParameter(membersName);
            if (p == null) return result; // not created yet = no members
            var list = ReferenceCodec.DecodeList(p.AsString(), out string err);
            if (err != null) error = $"'{membersName}' on {NameOf(owner)}: {err}";
            return ReferenceCodec.TrimTrailingEmpty(list);
        }

        /// <summary>Highest slot parameter the owner has (0 = none / list storage).</summary>
        internal static int SlotCapacity(Element owner, CollectionDto c, string membersName) =>
            !c.UsesSlots || owner == null ? 0 : SlotParameters(owner, membersName).Keys.DefaultIfEmpty(0).Max();

        /// <summary>Writes the owner's members (inside a transaction). Slots past the list are cleared. Returns an error,
        /// or null. Slot parameters must exist (EnsureParameters with the member count first).</summary>
        internal static string WriteMembers(Element owner, CollectionDto c, string membersName, IList<MemberRef> members)
        {
            var list = ReferenceCodec.TrimTrailingEmpty(members);
            if (c.UsesSlots)
            {
                var slots = SlotParameters(owner, membersName);
                for (int i = 0; i < list.Count; i++)
                    if (!slots.ContainsKey(i + 1))
                        return $"{NameOf(owner)} has no '{ReferenceCodec.SlotName(membersName, i + 1)}' parameter.";
                foreach (var kv in slots)
                {
                    string text = kv.Key <= list.Count ? ReferenceCodec.EncodeSlot(list[kv.Key - 1]) : "";
                    if ((kv.Value.AsString() ?? "") == text) continue;
                    if (kv.Value.IsReadOnly || !kv.Value.Set(text))
                        return $"Couldn't write '{kv.Value.Definition.Name}' on {NameOf(owner)}.";
                }
                return null;
            }
            var p = owner.LookupParameter(membersName);
            if (p == null || p.StorageType != StorageType.String) return $"{NameOf(owner)} has no text parameter '{membersName}'.";
            string json = ReferenceCodec.EncodeList(list);
            if ((p.AsString() ?? "") == json) return null;
            return !p.IsReadOnly && p.Set(json) ? null : $"Couldn't write '{membersName}' on {NameOf(owner)}.";
        }

        // ── overrides ────────────────────────────────────────────────────────

        internal static Overrides ReadOverrides(Element instance, string overrideName, out string error)
        {
            error = null;
            var p = string.IsNullOrEmpty(overrideName) ? null : instance?.LookupParameter(overrideName);
            if (p == null) return new Overrides();
            var o = OverrideCodec.Parse(p.AsString(), out string err);
            if (err != null) error = $"'{overrideName}' on {NameOf(instance)} ({instance.Id.GetIdValue()}): {err}";
            return o;
        }

        internal static string WriteOverrides(Element instance, string overrideName, Overrides o)
        {
            var p = string.IsNullOrEmpty(overrideName) ? null : instance?.LookupParameter(overrideName);
            if (p == null || p.StorageType != StorageType.String) return $"{NameOf(instance)} has no text parameter '{overrideName}'.";
            string json = OverrideCodec.Serialize(o);
            if ((p.AsString() ?? "") == json) return null;
            return !p.IsReadOnly && p.Set(json) ? null : $"Couldn't write '{overrideName}' on {NameOf(instance)}.";
        }

        // ── parameters ───────────────────────────────────────────────────────

        /// <summary>
        /// Makes sure the collection's parameters exist on its owner categories: members (type or instance parameter, as the
        /// collection lives; slots 1..slotCount) and the instance override parameter. Created ones are Perseus shared
        /// parameters, locked (only Perseus writes them); an existing parameter of the same name and kind is used as it is.
        /// Outside a transaction. Returns how many were created / extended.
        /// </summary>
        internal static int EnsureParameters(Document doc, CollectionDto c, int slotCount)
        {
            var cats = c.OwnerCategories != null && c.OwnerCategories.Count > 0
                ? new HashSet<string>(c.OwnerCategories, StringComparer.OrdinalIgnoreCase)
                : null;
            string group = ParameterNames.Group(doc);
            string members = MembersName(doc, c);
            var reqs = new List<SharedParameterManager.Requirement>();
            if (c.UsesSlots)
            {
                for (int n = 1; n <= Math.Max(1, slotCount); n++)
                    reqs.Add(new SharedParameterManager.Requirement
                    {
                        Name = ReferenceCodec.SlotName(members, n), Key = MembersKey(c), RecordName = members,
                        Categories = cats, Group = group, ReadOnly = true, TypeBinding = c.OnType,
                    });
            }
            else
            {
                reqs.Add(new SharedParameterManager.Requirement
                {
                    Name = members, Key = MembersKey(c), Categories = cats, Group = group, ReadOnly = true, TypeBinding = c.OnType,
                });
            }
            string over = OverrideName(doc, c);
            if (over != null)
                reqs.Add(new SharedParameterManager.Requirement
                {
                    Name = over, Key = OverridesKey(c), Categories = cats, Group = group, ReadOnly = true, TypeBinding = false,
                });
            int n2 = SharedParameterManager.EnsureParameters(doc, reqs);
            if (n2 > 0) Log.Info($"[CollectionStore] {c.Name}: {n2} parameter(s) created / extended ({string.Join(", ", reqs.Select(r => r.Name))}).");
            return n2;
        }
    }
}
