using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;
using StorageType = Autodesk.Revit.DB.StorageType;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Collection lists (models/CollectionListDto): each member's value of a parameter, in slot order, joined
    /// (collections/ListRollup) into a text parameter on the owner, e.g. every sign's "Sign Type" -> the pack type's
    /// "Sign Type" = "ST07, ST02". Owners on types list the type's members as they are; instance owners use their
    /// overrides. Kept current by collections/CollectionListController (edits), ui/CollectionsWindow (Apply) and Audit.
    /// Revit API context. Owners checked out by someone else are skipped (their Perseus writes them).
    /// </summary>
    internal static class CollectionLists
    {
        internal const string TransactionName = "Perseus: Update collection lists";

        internal static List<CollectionListDto> ListsOf(CollectionDto c) =>
            c?.Lists?.Where(l => l != null && l.IsValid).ToList() ?? new List<CollectionListDto>();

        /// <summary>The model's collections that have lists.</summary>
        internal static List<CollectionDto> WithLists(Document doc)
        {
            string guid = null;
            try { guid = ModelGuidStorage.TryGet(doc); } catch { /* no model id yet */ }
            var dto = guid == null ? null : CollectionsCache.Get(guid);
            return dto?.Collections?.Where(c => c.Enabled && ListsOf(c).Count > 0).ToList() ?? new List<CollectionDto>();
        }

        /// <summary>
        /// Creates the owner parameters that no owner has yet: Perseus shared text parameters, locked (only Perseus writes
        /// them), type parameters when the collection lives on types. An existing parameter of that name (e.g. from the
        /// company shared parameter file, or a family parameter) is used as it is. Outside a transaction.
        /// </summary>
        internal static int EnsureParameters(Document doc, CollectionDto c)
        {
            var lists = ListsOf(c);
            if (lists.Count == 0) return 0;
            var owners = CollectionStore.Owners(doc, c);
            if (owners.Count == 0) return 0;
            var cats = c.OwnerCategories != null && c.OwnerCategories.Count > 0
                ? new HashSet<string>(c.OwnerCategories, StringComparer.OrdinalIgnoreCase) : null;
            var reqs = new List<SharedParameterManager.Requirement>();
            foreach (var target in lists.Select(l => l.Target).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (owners.All(o => o.LookupParameter(target) != null)) continue;
                reqs.Add(new SharedParameterManager.Requirement
                {
                    Name = target, Categories = cats, Group = naming.ParameterNames.Group(doc), ReadOnly = true, TypeBinding = c.OnType,
                });
            }
            if (reqs.Count == 0) return 0;
            int n = SharedParameterManager.EnsureParameters(doc, reqs);
            if (n > 0) Log.Info($"[CollectionLists] {c.Name}: {n} list parameter(s) created / extended ({string.Join(", ", reqs.Select(r => r.Name))}).");
            return n;
        }

        /// <summary>Reads members of owners of one collection (names in use and the target index cached per run).</summary>
        internal sealed class Reader
        {
            private readonly Document _doc;
            internal readonly CollectionDto Collection;
            private readonly string _membersName;
            private readonly string _overrideName;
            private CollectionStore.TargetIndex _index;

            internal Reader(Document doc, CollectionDto c)
            {
                _doc = doc;
                Collection = c;
                _membersName = CollectionStore.MembersName(doc, c);
                _overrideName = c.OnType ? null : CollectionStore.OverrideName(doc, c);
            }

            private CollectionStore.TargetIndex Index => _index ?? (_index = new CollectionStore.TargetIndex(_doc, Collection));

            /// <summary>Member element per filled slot (slot order) and, on instance owners, that slot's overrides.</summary>
            internal List<(Element Member, Dictionary<string, string> Overrides)> Members(Element owner)
            {
                var result = new List<(Element, Dictionary<string, string>)>();
                var refs = CollectionStore.ReadMembers(owner, Collection, _membersName, out _);
                var overrides = _overrideName == null ? null : CollectionStore.ReadOverrides(owner, _overrideName, out _);
                var allowed = Collection.OverridableFields != null && Collection.OverridableFields.Count > 0
                    ? new HashSet<string>(Collection.OverridableFields, StringComparer.OrdinalIgnoreCase) : null;
                for (int i = 0; i < refs.Count; i++)
                {
                    if (refs[i].IsEmpty) continue;
                    var res = Index.Resolve(refs[i]);
                    var el = res.ElementId > 0 ? _doc.GetElement(RevitExtensions.CreateId(res.ElementId)) : null;
                    if (el == null) continue;
                    Dictionary<string, string> o = null;
                    if (overrides != null && !overrides.IsEmpty)
                    {
                        o = overrides.For(i + 1);
                        if (allowed != null) foreach (var k in o.Keys.Where(k => !allowed.Contains(k)).ToList()) o.Remove(k);
                    }
                    result.Add((el, o));
                }
                return result;
            }

            /// <summary>The ids of every member of every owner (to find owners affected by an edit).</summary>
            internal IEnumerable<long> MemberIds(Element owner) => Members(owner).Select(m => m.Member.Id.GetIdValue());
        }

        /// <summary>A member's value as text: its override, else the parameter as Revit displays it ("Name" = its name).</summary>
        internal static string ValueOf(Element member, Dictionary<string, string> overrides, string field)
        {
            if (overrides != null)
                foreach (var kv in overrides)
                    if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)) return kv.Value ?? "";
            var p = member.LookupParameter(field);
            if (p == null && !(member is ElementType))
            {
                // An instance member: fall back to its type's parameter.
                var typeId = member.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId) p = member.Document.GetElement(typeId)?.LookupParameter(field);
            }
            if (p == null)
                return string.Equals(field, "Name", StringComparison.OrdinalIgnoreCase) ? ElementNames.Of(member) : "";
            if (!p.HasValue) return "";
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString() ?? "";
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    var el = id == null || id == ElementId.InvalidElementId ? null : member.Document.GetElement(id);
                    return el != null ? ElementNames.Of(el) : p.AsValueString() ?? "";
                default: return p.AsValueString() ?? "";
            }
        }

        /// <summary>
        /// Writes one owner's list parameters (inside the caller's transaction). Returns how many changed; problems
        /// (missing / non-text parameter) go to <paramref name="errors"/>.
        /// </summary>
        internal static int WriteOwner(Reader reader, Element owner, List<string> errors)
        {
            var lists = ListsOf(reader.Collection);
            if (owner == null || lists.Count == 0) return 0;
            var members = reader.Members(owner);
            int changed = 0;
            foreach (var l in lists)
            {
                string text = ListRollup.Join(members.Select(m => ValueOf(m.Member, m.Overrides, l.MemberParameter.Trim())),
                                              l.Delimiter, l.SkipEmpty, l.Unique);
                var p = owner.LookupParameter(l.Target);
                if (p == null) { errors?.Add($"{CollectionStore.NameOf(owner)} has no '{l.Target}' parameter."); continue; }
                if (p.StorageType != StorageType.String) { errors?.Add($"'{l.Target}' on {CollectionStore.NameOf(owner)} isn't a text parameter."); continue; }
                if ((p.AsString() ?? "") == text) continue;
                if (p.IsReadOnly || !p.Set(text)) { errors?.Add($"Couldn't write '{l.Target}' on {CollectionStore.NameOf(owner)}."); continue; }
                changed++;
            }
            return changed;
        }

        internal sealed class Result
        {
            public int Owners;     // owners looked at
            public int Written;    // parameters changed
            public int NotOwned;   // owners checked out by someone else (skipped)
            public int Created;    // parameters created
            public readonly List<string> Errors = new List<string>();

            public string Summary() =>
                $"Collection lists: {Written} value(s) updated on {Owners} owner(s)" +
                (NotOwned > 0 ? $", {NotOwned} skipped (checked out by another user)" : "") +
                (Created > 0 ? $", {Created} parameter(s) created" : "") +
                (Errors.Count > 0 ? $"\nList problems: {string.Join("; ", Errors.Distinct().Take(5))}" + (Errors.Count > 5 ? "…" : "") : "");
        }

        /// <summary>
        /// Brings lists up to date in one transaction: every owner (<paramref name="changedIds"/> null, Audit), or only owners
        /// that changed or hold a changed member. Outside a transaction (creates missing parameters first).
        /// </summary>
        internal static Result Refresh(Document doc, ICollection<long> changedIds)
        {
            var result = new Result();
            var collections = WithLists(doc);
            if (collections.Count == 0 || doc.IsReadOnly) return result;

            var work = new List<(Reader Reader, List<Element> Owners)>();
            foreach (var c in collections)
            {
                var reader = new Reader(doc, c);
                var owners = CollectionStore.Owners(doc, c);
                if (changedIds != null)
                    owners = owners.Where(o => changedIds.Contains(o.Id.GetIdValue()) || reader.MemberIds(o).Any(changedIds.Contains)).ToList();
                if (owners.Count == 0) continue;
                work.Add((reader, owners));
            }
            if (work.Count == 0) return result;

            foreach (var w in work) result.Created += EnsureParameters(doc, w.Reader.Collection);

            using (var tx = new Transaction(doc, TransactionName))
            {
                tx.Start();
                foreach (var w in work)
                    foreach (var owner in w.Owners)
                    {
                        result.Owners++;
                        if (!IdAssigner.CanEdit(doc, owner.Id)) { result.NotOwned++; continue; }
                        result.Written += WriteOwner(w.Reader, doc.GetElement(owner.Id), result.Errors);
                    }
                if (result.Written > 0) tx.Commit(); else tx.RollBack();
            }
            if (result.Written > 0 || result.Errors.Count > 0 || changedIds == null)
                Log.Info($"[CollectionLists] {result.Summary().Replace("\n", " | ")}");
            return result;
        }
    }
}
