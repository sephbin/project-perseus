using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Adds the local parameter a mapping needs (stage 5 phase 2), as a project parameter (instance or type) bound to the
    /// family's category. Order: a parameter of that name already in the model with a compatible data type is used (and
    /// extended to the category); else a definition of that name in the company shared parameter file (optional setting);
    /// else the Perseus file (%AppData%, GUID from name + data type, group per model). The user's shared parameter file
    /// setting is swapped only for the call. Outside a transaction (own one, "Perseus: Add Parameter").
    /// </summary>
    internal static class ParamCreator
    {
        internal class Request
        {
            internal string Name;
            internal string Spec;           // ForgeTypeId id
            internal bool IsType;
            internal string Category;       // the local family's category name
            internal string SourceName;     // for the definition's description
            /// <summary>The model has a parameter of that name that doesn't fit: true = use it anyway (kind differs) / add a new one (type differs).</summary>
            internal bool Force;
        }

        internal class Result
        {
            internal ParamRef Ref;
            internal string Outcome;        // used / extended / created
            internal string Conflict;       // asks the user (re-run with Force)
            internal string Error;
        }

        // ── Settings: optional company shared parameter file (user setting, else machine-wide from IT) ──

        private class Settings { public string CompanyFile { get; set; } }

        private static string UserSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "shared_params.json");
        private static string MachineSettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectPerseus", "shared_params.json");

        /// <summary>The company shared parameter file to take definitions from (null = none): user setting, else machine-wide.</summary>
        internal static string CompanyFile
        {
            get
            {
                foreach (var p in new[] { UserSettingsPath, MachineSettingsPath })
                {
                    try
                    {
                        if (!File.Exists(p)) continue;
                        var s = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(p));
                        if (s != null && s.CompanyFile != null) return string.IsNullOrWhiteSpace(s.CompanyFile) ? null : s.CompanyFile;
                    }
                    catch (Exception ex) { Log.Info($"[ParamCreator] settings {p}: {ex.Message}"); }
                }
                return null;
            }
            set
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath));
                File.WriteAllText(UserSettingsPath, JsonConvert.SerializeObject(new Settings { CompanyFile = value ?? "" }, Formatting.Indented));
            }
        }

        // ── Adding ──

        internal static Result Ensure(Document doc, Request req)
        {
            var res = new Result();
            req.Name = (req.Name ?? "").Trim();
            if (req.Name.Length == 0) { res.Error = "Give the parameter a name."; return res; }
            var cat = doc.Settings.Categories.Cast<Category>().FirstOrDefault(c => string.Equals(c.Name, req.Category, StringComparison.OrdinalIgnoreCase));
            if (cat == null) { res.Error = $"Category '{req.Category}' not found in this model."; return res; }
            if (!cat.AllowsBoundParameters) { res.Error = $"{cat.Name} doesn't take project parameters."; return res; }
            var spec = SpecOrText(req.Spec);
            string want = ParamCompat.SpecKey(spec.TypeId);

            // 1. Already in the model under that name?
            var bound = new List<Tuple<Definition, ElementBinding>>();
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (string.Equals(it.Key?.Name, req.Name, StringComparison.OrdinalIgnoreCase) && it.Current is ElementBinding eb) bound.Add(Tuple.Create(it.Key, eb));
            var fit = bound.FirstOrDefault(b => ParamCompat.SpecKey(Spec(b.Item1)?.TypeId) == want && (b.Item2 is TypeBinding) == req.IsType);
            var otherKind = bound.FirstOrDefault(b => ParamCompat.SpecKey(Spec(b.Item1)?.TypeId) == want && (b.Item2 is TypeBinding) != req.IsType);
            if (fit == null && otherKind != null)
            {
                if (!req.Force)
                {
                    res.Conflict = $"The model already has '{req.Name}' as {(otherKind.Item2 is TypeBinding ? "a type" : "an instance")} parameter. Revit can't switch it to " +
                                   $"{(req.IsType ? "type" : "instance")} without losing its values. Use the existing one as it is?";
                    return res;
                }
                fit = otherKind;
            }
            if (fit == null && bound.Count > 0 && !req.Force)
            {
                res.Conflict = $"The model already has a parameter called '{req.Name}' ({Label(Spec(bound[0].Item1)) ?? "another data type"}), which doesn't fit " +
                               $"{Label(spec) ?? "this value"}. Add a second '{req.Name}' with the new data type anyway (two parameters with one name can confuse people)?";
                return res;
            }
            if (fit != null)
            {
                bool has = fit.Item2.Categories.Contains(cat);
                if (!has)
                {
                    using (var tx = new Transaction(doc, "Perseus: Add Parameter"))
                    {
                        tx.Start();
                        var set = doc.Application.Create.NewCategorySet();
                        foreach (Category c in fit.Item2.Categories) set.Insert(c);
                        set.Insert(cat);
                        ElementBinding nb = fit.Item2 is TypeBinding ? (ElementBinding)doc.Application.Create.NewTypeBinding(set) : doc.Application.Create.NewInstanceBinding(set);
                        doc.ParameterBindings.ReInsert(fit.Item1, nb);
                        tx.Commit();
                    }
                }
                res.Outcome = has ? "used" : "extended";
                res.Ref = RefOf(doc, fit.Item1, fit.Item2 is TypeBinding);
                Log.Info($"[ParamCreator] '{req.Name}' {res.Outcome} for {cat.Name}");
                return res;
            }

            // 2. / 3. A definition: company file (by name, same data type), else the Perseus file (GUID from name + data type).
            var app = doc.Application;
            string previous = app.SharedParametersFilename;
            try
            {
                ExternalDefinition def = null;
                string company = CompanyFile;
                if (company != null && File.Exists(company))
                {
                    app.SharedParametersFilename = company;
                    var cf = app.OpenSharedParameterFile();
                    def = cf?.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<ExternalDefinition>())
                             .FirstOrDefault(d => string.Equals(d.Name, req.Name, StringComparison.OrdinalIgnoreCase) && ParamCompat.SpecKey(Spec(d)?.TypeId) == want);
                    if (def != null) Log.Info($"[ParamCreator] '{req.Name}' from the company file {company}");
                }
                if (def == null)
                {
                    SharedParameterManager.EnsureFileExists();
                    app.SharedParametersFilename = SharedParameterManager.FilePath;
                    var pf = app.OpenSharedParameterFile();
                    if (pf == null) { res.Error = "Couldn't open the Perseus shared parameter file."; return res; }
                    var guid = ParamCompat.GuidFor(req.Name, spec.TypeId);
                    def = pf.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<ExternalDefinition>()).FirstOrDefault(d => d.GUID == guid);
                    if (def == null)
                    {
                        string groupName = GroupName(doc);
                        var group = pf.Groups.get_Item(groupName) ?? pf.Groups.Create(groupName);
                        def = (ExternalDefinition)group.Definitions.Create(new ExternalDefinitionCreationOptions(req.Name, spec)
                        {
                            GUID = guid, Visible = true, UserModifiable = true,
                            Description = string.IsNullOrEmpty(req.SourceName) ? "Added by Perseus (family mapping)" : $"Added by Perseus: mapped from the consultant's '{req.SourceName}'",
                        });
                    }
                }
                using (var tx = new Transaction(doc, "Perseus: Add Parameter"))
                {
                    tx.Start();
                    var set = app.Create.NewCategorySet();
                    set.Insert(cat);
                    ElementBinding binding = req.IsType ? (ElementBinding)app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);
                    if (!InsertOther(doc, def, binding)) { tx.RollBack(); res.Error = $"Revit refused to add '{req.Name}' (a parameter with its GUID may already exist under another name)."; return res; }
                    if (!req.IsType)
                    {
                        // Mapped values differ per element: allow them to differ inside groups too.
                        try { (SharedParameterElement.Lookup(doc, def.GUID)?.GetDefinition())?.SetAllowVaryBetweenGroups(doc, true); } catch { }
                    }
                    tx.Commit();
                }
                res.Outcome = "created";
                res.Ref = new ParamRef
                {
                    Name = def.Name, Guid = def.GUID.ToString(), Storage = Storage(spec), Kind = Kind(spec), Spec = spec.TypeId, SpecLabel = Label(spec),
                    IsType = req.IsType, Origin = "shared",
                };
                Log.Info($"[ParamCreator] '{def.Name}' ({Label(spec)}, {(req.IsType ? "type" : "instance")}) created for {cat.Name}, GUID {def.GUID}");
                return res;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ParamCreator] '{req.Name}': {ex.Message}");
                res.Error = ex.Message;
                return res;
            }
            finally
            {
                try { app.SharedParametersFilename = previous ?? ""; }
                catch (Exception ex) { Log.Warn($"[ParamCreator] couldn't restore the shared parameter file path: {ex.Message}"); }
            }
        }

        private static bool InsertOther(Document doc, Definition def, ElementBinding binding)
        {
#if REVIT2024_OR_GREATER
            return doc.ParameterBindings.Insert(def, binding, new ForgeTypeId());
#else
            return doc.ParameterBindings.Insert(def, binding, BuiltInParameterGroup.INVALID);
#endif
        }

        /// <summary>"Perseus · &lt;model&gt;" (shared parameter file groups can't contain some characters).</summary>
        private static string GroupName(Document doc)
        {
            var title = new string((doc.Title ?? "model").Select(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '_' || ch == '-' ? ch : '_').ToArray()).Trim();
            return ("Perseus " + title).Substring(0, Math.Min(60, 8 + title.Length));
        }

        private static ForgeTypeId SpecOrText(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    var spec = new ForgeTypeId(id);
                    if (SpecUtils.IsSpec(spec) && (UnitUtils.IsMeasurableSpec(spec) || spec == SpecTypeId.Number || spec == SpecTypeId.Int.Integer ||
                                                   spec == SpecTypeId.Boolean.YesNo || spec == SpecTypeId.String.Text)) return spec;
                }
                catch { }
            }
            return SpecTypeId.String.Text;
        }

        private static ForgeTypeId Spec(Definition d) { try { return d?.GetDataType(); } catch { return null; } }

        private static string Label(ForgeTypeId s) { try { return s == null || string.IsNullOrEmpty(s.TypeId) ? null : LabelUtils.GetLabelForSpec(s); } catch { return null; } }

        private static string Storage(ForgeTypeId s) =>
            s == SpecTypeId.String.Text ? "String" : s == SpecTypeId.Boolean.YesNo || s == SpecTypeId.Int.Integer ? "Integer" : "Double";

        private static string Kind(ForgeTypeId s) =>
            s == SpecTypeId.String.Text ? ParamCompat.Text : s == SpecTypeId.Boolean.YesNo ? ParamCompat.YesNo : s == SpecTypeId.Int.Integer ? ParamCompat.Integer
            : s == SpecTypeId.Number ? ParamCompat.Number : ParamCompat.Measure;

        private static ParamRef RefOf(Document doc, Definition def, bool isType)
        {
            var spec = Spec(def);
            string guid = SharedGuid(doc, def);
            return new ParamRef
            {
                Name = def.Name, Guid = guid, Storage = spec == null ? "String" : Storage(spec), Kind = spec == null ? ParamCompat.Text : Kind(spec),
                Spec = spec?.TypeId, SpecLabel = Label(spec), IsType = isType, Origin = guid != null ? "shared" : "project",
            };
        }

        /// <summary>The GUID of a bound project parameter if it's shared (bound keys are InternalDefinitions: via its ParameterElement).</summary>
        internal static string SharedGuid(Document doc, Definition def)
        {
            if (def is ExternalDefinition ed) return ed.GUID.ToString();
            try { return (doc.GetElement((def as InternalDefinition)?.Id ?? ElementId.InvalidElementId) as SharedParameterElement)?.GuidValue.ToString(); }
            catch { return null; }
        }
    }
}
