using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.util;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Creates and binds the shared parameters Perseus writes IDs into.
    ///
    /// Definitions live in a Perseus-managed shared parameter file
    /// (%AppData%\ProjectPerseus\Perseus_SharedParams.txt) so the firm's own file is never touched.
    /// GUIDs are derived from the parameter name, so every user and every model gets the same GUID
    /// and parameters stay consistent across worksets, transfers and schedules.
    /// The user's shared parameter file path is swapped only for the duration of the call and restored.
    /// Locked rule parameters (IdRuleDto.ReadOnly) are created with UserModifiable = false (greyed out in Properties and
    /// schedules, still written by Perseus) under their own GUID and file group; Revit can't change that flag on an
    /// existing parameter, so SwitchLock replaces the parameter and copies its values.
    /// </summary>
    internal static class SharedParameterManager
    {
        internal const string PerseusIdName = "Perseus_ID";
        private const string GroupName = "Perseus";
        private const string LockedGroupName = "Perseus locked";

        private class Requirement
        {
            public string Name;
            public HashSet<string> Categories; // null = every model category that accepts parameters
            public string Group = "other"; // key from Django's ID_PARAMETER_GROUPS; Perseus parameters default to "Other"
            public bool ReadOnly;          // UserModifiable = false (locked against hand edits)
        }

#if REVIT2024_OR_GREATER
        private static ForgeTypeId GroupFor(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "identity_data": return GroupTypeId.IdentityData;
                case "general": return GroupTypeId.General;
                case "data": return GroupTypeId.Data;
                case "text": return GroupTypeId.Text;
                case "construction": return GroupTypeId.Construction;
                case "graphics": return GroupTypeId.Graphics;
                case "constraints": return GroupTypeId.Constraints;
                case "phasing": return GroupTypeId.Phasing;
                case "materials": return GroupTypeId.Materials;
                case "visibility": return GroupTypeId.Visibility;
                case "geometry": return GroupTypeId.Geometry;
                case "ifc": return GroupTypeId.Ifc;
                default: return new ForgeTypeId(); // "other" / unknown: empty id is the "Other" group
            }
        }
#else
        private static BuiltInParameterGroup GroupFor(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "identity_data": return BuiltInParameterGroup.PG_IDENTITY_DATA;
                case "general": return BuiltInParameterGroup.PG_GENERAL;
                case "data": return BuiltInParameterGroup.PG_DATA;
                case "text": return BuiltInParameterGroup.PG_TEXT;
                case "construction": return BuiltInParameterGroup.PG_CONSTRUCTION;
                case "graphics": return BuiltInParameterGroup.PG_GRAPHICS;
                case "constraints": return BuiltInParameterGroup.PG_CONSTRAINTS;
                case "phasing": return BuiltInParameterGroup.PG_PHASING;
                case "materials": return BuiltInParameterGroup.PG_MATERIALS;
                case "visibility": return BuiltInParameterGroup.PG_VISIBILITY;
                case "geometry": return BuiltInParameterGroup.PG_GEOMETRY;
                case "ifc": return BuiltInParameterGroup.PG_IFC;
                default: return BuiltInParameterGroup.INVALID; // "other" / unknown
            }
        }
#endif

        internal static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ProjectPerseus", "Perseus_SharedParams.txt");

        internal static Guid GuidFor(string name) => GuidFor(name, false);

        /// <summary>Deterministic GUID; a locked parameter of the same name is a different parameter (different GUID).</summary>
        internal static Guid GuidFor(string name, bool readOnly)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(
                    (readOnly ? "ProjectPerseus.SharedParameter.Locked:" : "ProjectPerseus.SharedParameter:") +
                    name.Trim().ToLowerInvariant()));
                return new Guid(hash);
            }
        }

        // The Perseus file definition for a parameter (created when missing). Locked ones live in their own group so
        // both spellings of a name can sit in the file.
        private static Definition DefinitionFor(DefinitionFile file, string name, bool readOnly)
        {
            string groupName = readOnly ? LockedGroupName : GroupName;
            DefinitionGroup group = file.Groups.get_Item(groupName) ?? file.Groups.Create(groupName);
            Guid guid = GuidFor(name, readOnly);
            var existing = group.Definitions.get_Item(name);
            if (existing is ExternalDefinition found && (found.GUID == guid || !readOnly)) return existing; // unlocked: as before
            if (existing != null)
                throw new InvalidOperationException($"The Perseus shared parameter file holds another '{name}' in group '{groupName}'.");
            return group.Definitions.Create(new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
            {
                GUID = guid,
                Visible = true,
                UserModifiable = !readOnly,
            });
        }

        // Runs body with the Perseus shared parameter file open, restoring the user's file path afterwards.
        private static T WithPerseusFile<T>(Document doc, Func<DefinitionFile, T> body)
        {
            var app = doc.Application;
            string previousFile = app.SharedParametersFilename;
            try
            {
                EnsureFileExists();
                app.SharedParametersFilename = FilePath;
                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null) throw new InvalidOperationException("Could not open the Perseus shared parameter file.");
                return body(file);
            }
            finally
            {
                try { app.SharedParametersFilename = previousFile ?? ""; }
                catch (Exception ex) { Log.Warn($"[SharedParameterManager] Could not restore shared parameter file path: {ex.Message}"); }
            }
        }

        /// <summary>A rule parameter whose lock state differs from the rule's (only Perseus's own parameters).</summary>
        internal class LockMismatch
        {
            public string Name;
            public bool WantReadOnly;
        }

        /// <summary>
        /// Enabled rules whose bound parameter is Perseus's own but locked the other way (the rule's Read-only option was
        /// changed after the parameter was created). Parameters from another shared parameter file are never touched.
        /// </summary>
        internal static List<LockMismatch> LockMismatches(Document doc, IdRulesDto rules)
        {
            var bound = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (SharedParameterElement spe in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)))
                bound[spe.Name] = spe.GuidValue;
            var result = new List<LockMismatch>();
            foreach (var r in rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName)))
            {
                string name = r.ParameterName.Trim();
                if (bound.TryGetValue(name, out var g) && g == GuidFor(name, !r.ReadOnly))
                    result.Add(new LockMismatch { Name = name, WantReadOnly = r.ReadOnly });
            }
            return result;
        }

        /// <summary>
        /// Replaces Perseus parameter <paramref name="name"/> with its locked (or unlocked) twin: same name, categories,
        /// group and vary-by-group setting, new GUID, every value copied across; one undo step. Refused (message
        /// returned, nothing changed) while any element holding a value is checked out by someone else or out of date
        /// with central. Schedules, filters and tags that used the old parameter lose it. Outside a transaction.
        /// </summary>
        internal static string SwitchLock(Document doc, string name, bool readOnly, out int copied, out int failed)
        {
            copied = 0;
            failed = 0;
            Guid oldGuid = GuidFor(name, !readOnly), newGuid = GuidFor(name, readOnly);
            var spe = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>()
                .FirstOrDefault(s => s.GuidValue == oldGuid);
            if (spe == null) return $"'{name}' isn't a Perseus parameter in this model.";
            InternalDefinition oldDef = spe.GetDefinition();
            if (!(doc.ParameterBindings.get_Item(oldDef) is InstanceBinding binding)) return $"'{name}' isn't bound as an instance parameter.";
            var cats = binding.Categories.Cast<Category>().ToList();
            bool varies = oldDef.VariesAcrossGroups;
#if REVIT2024_OR_GREATER
            var group = oldDef.GetGroupTypeId();
#else
            var group = oldDef.ParameterGroup;
#endif

            var values = new Dictionary<ElementId, string>();
            if (cats.Count > 0)
            {
                foreach (Autodesk.Revit.DB.Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                             .WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList())))
                {
                    string v = el.get_Parameter(oldGuid)?.AsString();
                    if (!string.IsNullOrEmpty(v)) values[el.Id] = v;
                }
            }

            if (doc.IsWorkshared)
            {
                var owners = new SortedSet<string>();
                int busy = 0, stale = 0;
                foreach (var id in values.Keys)
                {
                    if (WorksharingUtils.GetCheckoutStatus(doc, id, out string owner) == CheckoutStatus.OwnedByOtherUser)
                    {
                        busy++;
                        if (!string.IsNullOrEmpty(owner)) owners.Add(owner);
                        continue;
                    }
                    var st = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                    if (st == ModelUpdatesStatus.UpdatedInCentral || st == ModelUpdatesStatus.DeletedInCentral) stale++;
                }
                if (busy > 0)
                    return $"{busy} element(s) with a '{name}' value are checked out by {string.Join(", ", owners.DefaultIfEmpty("another user"))}. " +
                           "Ask them to sync and relinquish, then try again.";
                if (stale > 0)
                    return $"{stale} element(s) with a '{name}' value changed in central since you last synced. Reload Latest, then try again.";
            }

            int ok = 0, bad = 0;
            string error = WithPerseusFile(doc, file =>
            {
                using (var tg = new TransactionGroup(doc, (readOnly ? "Perseus: Lock " : "Perseus: Unlock ") + name))
                {
                    tg.Start();
                    using (var tx = new Transaction(doc, "Perseus: Remove old parameter"))
                    {
                        tx.Start();
                        doc.ParameterBindings.Remove(oldDef);
                        doc.Delete(spe.Id);
                        if (tx.Commit() != TransactionStatus.Committed) { tg.RollBack(); return "Revit refused to remove the old parameter."; }
                    }
                    using (var tx = new Transaction(doc, "Perseus: Add parameter"))
                    {
                        tx.Start();
                        CategorySet set = doc.Application.Create.NewCategorySet();
                        foreach (var c in cats) set.Insert(c);
                        Definition def = DefinitionFor(file, name, readOnly);
                        if (!doc.ParameterBindings.Insert(def, doc.Application.Create.NewInstanceBinding(set), group))
                        { tx.RollBack(); tg.RollBack(); return "Revit refused to add the new parameter."; }
                        if (varies)
                        {
                            var added = SharedParameterElement.Lookup(doc, newGuid)?.GetDefinition();
                            added?.SetAllowVaryBetweenGroups(doc, true);
                        }
                        if (tx.Commit() != TransactionStatus.Committed) { tg.RollBack(); return "Revit refused to add the new parameter."; }
                    }
                    using (var tx = new Transaction(doc, "Perseus: Copy values"))
                    {
                        tx.Start();
                        foreach (var kv in values)
                        {
                            try
                            {
                                var p = doc.GetElement(kv.Key)?.get_Parameter(newGuid);
                                if (p != null && p.Set(kv.Value)) ok++; else bad++;
                            }
                            catch { bad++; }
                        }
                        if (tx.Commit() != TransactionStatus.Committed) { tg.RollBack(); return "Revit refused to copy the values across."; }
                    }
                    tg.Assimilate();
                    return null;
                }
            });
            copied = ok;
            failed = bad;
            if (error == null)
                Log.Info($"[SharedParameterManager] '{name}' {(readOnly ? "locked" : "unlocked")}: {ok} value(s) copied, {bad} failed.");
            return error;
        }

        /// <summary>
        /// Maps each requested parameter name to the GUID of the shared parameter bound in this document.
        /// Names that are not bound as shared parameters are omitted.
        /// </summary>
        internal static Dictionary<string, Guid> ResolveGuids(Document doc, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            // Shared parameters are identified by SharedParameterElement; the binding map's keys are not
            // reliably ExternalDefinition once bound to a project, so don't depend on that.
            foreach (SharedParameterElement spe in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)))
            {
                if (wanted.Contains(spe.Name)) result[spe.Name] = spe.GuidValue;
            }
            return result;
        }

        /// <summary>
        /// Makes sure Perseus_ID and every enabled rule parameter exists and is bound (instance) to its
        /// categories. Returns the number of parameters created or extended; throws on failure.
        /// Must be called outside an open transaction.
        /// </summary>
        internal static int Ensure(Document doc, IdRulesDto rules)
        {
            var requirements = new List<Requirement>
            {
                new Requirement
                {
                    Name = PerseusIdName,
                    Group = "other",
                    Categories = rules.PerseusId.ApplyToAll
                        ? null
                        : new HashSet<string>(rules.PerseusId.Categories, StringComparer.OrdinalIgnoreCase),
                },
            };
            foreach (var r in rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName) &&
                                                      IdAssigner.IsUsable(r)))
            {
                requirements.Add(new Requirement
                {
                    Name = r.ParameterName.Trim(),
                    Group = r.ParameterGroup,
                    ReadOnly = r.ReadOnly,
                    Categories = r.ApplyToAll
                        ? null
                        : new HashSet<string>(r.Categories, StringComparer.OrdinalIgnoreCase),
                });
            }

            return EnsureRequirements(doc, requirements);
        }

        internal const string LinkSourceName = "Perseus_LinkSource";
        internal const string LinkStatusName = "Perseus_LinkStatus";
        internal const string LinkDecisionName = "Perseus_LinkDecision";
        internal const string LinkCommentName = "Perseus_LinkComment";
        internal const string LinkAssigneeName = "Perseus_LinkAssignee";

        /// <summary>
        /// Link Sync's parameters on our mapped families: Perseus_LinkSource (link Â· consultant element id),
        /// Perseus_LinkStatus, Perseus_LinkDecision (accepted / rejected Â· who Â· when), Perseus_LinkComment (latest),
        /// Perseus_LinkAssignee; instance project parameters (shared, Perseus file) bound to these categories (added to
        /// existing bindings). Outside a transaction. Returns how many were created / extended.
        /// </summary>
        internal static int EnsureLinkSyncParameters(Document doc, IEnumerable<string> categoryNames)
        {
            var cats = new HashSet<string>(categoryNames.Where(c => !string.IsNullOrEmpty(c)), StringComparer.OrdinalIgnoreCase);
            if (cats.Count == 0) return 0;
            return EnsureRequirements(doc, new List<Requirement>
            {
                new Requirement { Name = LinkSourceName, Categories = cats, Group = "other" },
                new Requirement { Name = LinkStatusName, Categories = cats, Group = "other" },
                new Requirement { Name = LinkDecisionName, Categories = cats, Group = "other" },
                new Requirement { Name = LinkCommentName, Categories = cats, Group = "other" },
                new Requirement { Name = LinkAssigneeName, Categories = cats, Group = "other" },
            }) + MoveToOther(doc, new[] { LinkSourceName, LinkStatusName });
        }

        /// <summary>
        /// Puts Perseus parameters bound earlier under another group into "Other" (the Link Sync ones were first made under
        /// Identity Data). 2024+ only (group of a bound parameter by ForgeTypeId). Returns how many moved.
        /// </summary>
        private static int MoveToOther(Document doc, IEnumerable<string> names)
        {
#if REVIT2024_OR_GREATER
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var todo = new List<InternalDefinition>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition def && wanted.Contains(def.Name) && !string.IsNullOrEmpty(def.GetGroupTypeId()?.TypeId))
                    todo.Add(def);
            }
            if (todo.Count == 0) return 0;
            int moved = 0;
            using (var tx = new Transaction(doc, "Perseus: Parameters to Other"))
            {
                tx.Start();
                foreach (var def in todo)
                {
                    try { def.SetGroupTypeId(new ForgeTypeId()); moved++; }
                    catch (Exception ex) { Log.Info($"[SharedParameterManager] couldn't move '{def.Name}' to Other: {ex.Message}"); }
                }
                tx.Commit();
            }
            return moved;
#else
            return 0;
#endif
        }

        private static int EnsureRequirements(Document doc, List<Requirement> requirements)
        {
            var bindable = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (c.CategoryType == CategoryType.Model && c.AllowsBoundParameters)
                    bindable.Add(c);
            }

            var existing = new Dictionary<string, (Definition Def, ElementBinding Binding)>(StringComparer.OrdinalIgnoreCase);
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!existing.ContainsKey(it.Key.Name))
                    existing[it.Key.Name] = (it.Key, it.Current as ElementBinding);
            }

            // Work out what has to change before touching the shared parameter file or opening a transaction.
            var work = new List<(Requirement Req, List<Category> Target, Definition ExistingDef, ElementBinding ExistingBinding)>();
            foreach (var req in requirements)
            {
                if (req.Categories != null && req.Categories.Count == 0) continue; // e.g. Perseus_ID with no rules: off
                var target = bindable.Where(c => req.Categories == null || req.Categories.Contains(c.Name)).ToList();
                if (target.Count == 0)
                {
                    Log.Warn($"[SharedParameterManager] No bindable categories for '{req.Name}' â€” skipped.");
                    continue;
                }

                if (existing.TryGetValue(req.Name, out var found))
                {
                    if (!(found.Binding is InstanceBinding))
                    {
                        Log.Warn($"[SharedParameterManager] '{req.Name}' already exists as a type/other parameter â€” left unchanged.");
                        continue;
                    }
                    var bound = new HashSet<ElementId>(found.Binding.Categories.Cast<Category>().Select(c => c.Id));
                    var missing = target.Where(c => !bound.Contains(c.Id)).ToList();
                    if (missing.Count == 0) continue;
                    work.Add((req, found.Binding.Categories.Cast<Category>().Concat(missing).ToList(), found.Def, found.Binding));
                }
                else
                {
                    work.Add((req, target, null, null));
                }
            }

            if (work.Count == 0) return EnsureVaryByGroup(doc, requirements.Select(r => r.Name));

            var app = doc.Application;
            WithPerseusFile(doc, file =>
            {
                using (var tx = new Transaction(doc, "Perseus: Add Parameters"))
                {
                    tx.Start();
                    foreach (var item in work)
                    {
                        CategorySet set = app.Create.NewCategorySet();
                        foreach (var c in item.Target) set.Insert(c);
                        InstanceBinding binding = app.Create.NewInstanceBinding(set);

                        if (item.ExistingDef != null)
                        {
                            doc.ParameterBindings.ReInsert(item.ExistingDef, binding);
                            Log.Info($"[SharedParameterManager] Extended '{item.Req.Name}' to {item.Target.Count} categories.");
                            continue;
                        }

                        Definition def = DefinitionFor(file, item.Req.Name, item.Req.ReadOnly);

                        // Group applies on creation only; Perseus_ID is automatic so it sits in "Other".
                        doc.ParameterBindings.Insert(def, binding, GroupFor(item.Req.Group));
                        Log.Info($"[SharedParameterManager] Created '{item.Req.Name}' bound to {item.Target.Count} categories.");
                    }
                    tx.Commit();
                }
                return 0;
            });

            return work.Count + EnsureVaryByGroup(doc, requirements.Select(r => r.Name));
        }

        /// <summary>
        /// Turns on "Values can vary by group instance" for the ID parameters. Without it, elements inside Revit
        /// groups must share one value across every instance of the group, so writing a unique ID to each member
        /// clashes. Also fixes parameters created before this was set. Returns how many were changed.
        /// </summary>
        private static int EnsureVaryByGroup(Document doc, IEnumerable<string> names)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var todo = new List<InternalDefinition>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition def && wanted.Contains(def.Name) && it.Current is InstanceBinding &&
                    !def.VariesAcrossGroups)
                    todo.Add(def);
            }
            if (todo.Count == 0) return 0;

            int changed = 0;
            using (var tx = new Transaction(doc, "Perseus: ID Parameters Vary By Group"))
            {
                tx.Start();
                foreach (var def in todo)
                {
                    try { def.SetAllowVaryBetweenGroups(doc, true); changed++; }
                    catch (Exception ex) { Log.Warn($"[SharedParameterManager] Could not set vary-by-group on '{def.Name}': {ex.Message}"); }
                }
                tx.Commit();
            }
            Log.Info($"[SharedParameterManager] Enabled 'values can vary by group instance' on {changed} parameter(s).");
            return changed;
        }

        internal static void EnsureFileExists()
        {
            string path = FilePath;
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string header =
                "# This is a Revit shared parameter file.\r\n" +
                "# Do not edit manually.\r\n" +
                "*META\tVERSION\tMINVERSION\r\n" +
                "META\t2\t1\r\n" +
                "*GROUP\tID\tNAME\r\n" +
                "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n";
            File.WriteAllText(path, header, new UnicodeEncoding(false, true));
        }
    }
}
