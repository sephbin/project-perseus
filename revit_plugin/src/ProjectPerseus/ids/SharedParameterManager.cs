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
    /// </summary>
    internal static class SharedParameterManager
    {
        internal const string PerseusIdName = "Perseus_ID";
        private const string GroupName = "Perseus";

        private class Requirement
        {
            public string Name;
            public HashSet<string> Categories; // null = every model category that accepts parameters
            public string Group = "identity_data"; // key from Django's ID_PARAMETER_GROUPS
        }

#if REVIT2024_OR_GREATER
        private static ForgeTypeId GroupFor(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "other": return new ForgeTypeId(); // empty id is the "Other" group
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
                default: return GroupTypeId.IdentityData;
            }
        }
#else
        private static BuiltInParameterGroup GroupFor(string key)
        {
            switch ((key ?? "").ToLowerInvariant())
            {
                case "other": return BuiltInParameterGroup.INVALID;
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
                default: return BuiltInParameterGroup.PG_IDENTITY_DATA;
            }
        }
#endif

        internal static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ProjectPerseus", "Perseus_SharedParams.txt");

        internal static Guid GuidFor(string name)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(
                    "ProjectPerseus.SharedParameter:" + name.Trim().ToLowerInvariant()));
                return new Guid(hash);
            }
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
            var requirements = new List<Requirement> { new Requirement { Name = PerseusIdName, Group = "other" } };
            foreach (var r in rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName) &&
                                                      IdPattern.Parse(r.Pattern) != null))
            {
                requirements.Add(new Requirement
                {
                    Name = r.ParameterName.Trim(),
                    Group = r.ParameterGroup,
                    Categories = r.ApplyToAll
                        ? null
                        : new HashSet<string>(r.Categories, StringComparer.OrdinalIgnoreCase),
                });
            }

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
                var target = bindable.Where(c => req.Categories == null || req.Categories.Contains(c.Name)).ToList();
                if (target.Count == 0)
                {
                    Log.Warn($"[SharedParameterManager] No bindable categories for '{req.Name}' — skipped.");
                    continue;
                }

                if (existing.TryGetValue(req.Name, out var found))
                {
                    if (!(found.Binding is InstanceBinding))
                    {
                        Log.Warn($"[SharedParameterManager] '{req.Name}' already exists as a type/other parameter — left unchanged.");
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
            string previousFile = app.SharedParametersFilename;
            try
            {
                EnsureFileExists();
                app.SharedParametersFilename = FilePath;
                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null) throw new InvalidOperationException("Could not open the Perseus shared parameter file.");
                DefinitionGroup group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);

                using (var tx = new Transaction(doc, "Perseus: Add ID Parameters"))
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

                        Definition def = group.Definitions.get_Item(item.Req.Name) ??
                            group.Definitions.Create(new ExternalDefinitionCreationOptions(item.Req.Name, SpecTypeId.String.Text)
                            {
                                GUID = GuidFor(item.Req.Name),
                                Visible = true,
                                UserModifiable = true,
                            });

                        // Group applies on creation only; Perseus_ID is automatic so it sits in "Other".
                        doc.ParameterBindings.Insert(def, binding, GroupFor(item.Req.Group));
                        Log.Info($"[SharedParameterManager] Created '{item.Req.Name}' bound to {item.Target.Count} categories.");
                    }
                    tx.Commit();
                }
            }
            finally
            {
                try { app.SharedParametersFilename = previousFile ?? ""; }
                catch (Exception ex) { Log.Warn($"[SharedParameterManager] Could not restore shared parameter file path: {ex.Message}"); }
            }

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

        private static void EnsureFileExists()
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
