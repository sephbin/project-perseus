using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using ProjectPerseus.models;
using ProjectPerseus.util;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Supplies the values for Auto Parameters pattern tokens: {CategoryPrefix}, {Workset} and
    /// {Level|Host|Room|Space|ToRoom|FromRoom|Type|Group|Project|Element.ParamName} (Project = the model's Project
    /// Information; ToRoom / FromRoom = a door's or window's rooms; Element = the element itself) and fallbacks {A|B}.
    /// Returns null when a value is unavailable (no related element, parameter missing or empty): ID rules then skip
    /// the element, value rules clear their value.
    /// </summary>
    internal sealed class RelatedValueResolver
    {
        private readonly Document _doc;
        private readonly IdRulesDto _rules;

        internal RelatedValueResolver(Document doc, IdRulesDto rules)
        {
            _doc = doc;
            _rules = rules;
        }

        internal Func<string, string> For(Element el) => token => Resolve(el, token, null);

        /// <summary>
        /// Like <see cref="For(Element)"/>, but {Element.Param} reads <paramref name="pending"/> first: values another
        /// rule is about to write in the same run (a door's room number copied before its ID is numbered with it).
        /// </summary>
        internal Func<string, string> For(Element el, IDictionary<string, string> pending) => token => Resolve(el, token, pending);

        /// <summary>
        /// The related elements an element's value is read from (its level, room, host, type, group), so a change to
        /// one of them can refresh the elements that copy it. Project Information is not listed: every element using
        /// {Project.*} depends on it, so that case is handled per rule (IdAssigner.ProjectDependents).
        /// </summary>
        internal void AddDependencies(Element el, IEnumerable<string> tokens, ISet<long> into)
        {
            foreach (var token in tokens.SelectMany(IdPattern.Alternatives))
            {
                int dot = token.IndexOf('.');
                if (dot < 0) continue;
                string kind = token.Substring(0, dot);
                if (kind == "Project" || kind == "Element") continue;
                try
                {
                    var related = Related(el, kind);
                    if (related != null) into.Add(ProjectPerseus.revit.RevitExtensions.GetIdValue(related.Id));
                }
                catch { /* unresolvable now; nothing to depend on */ }
            }
        }

        /// <summary>
        /// Every parameter of a related object ("Type", "ToRoom"...) by name, for Jinja templates (util/JinjaTemplate):
        /// text as shown in Revit, numbers (lengths, areas, integers) as numbers when their display parses as one. "Name" =
        /// the element's name (Project: the project's name / number). Element also sees values planned in this run.
        /// Null when there is no such object (no room...).
        /// </summary>
        internal IDictionary<string, object> ParametersOf(Element el, string kind, IDictionary<string, string> pending)
        {
            Element related;
            try { related = Related(el, kind); }
            catch { return null; }
            if (related == null) return null;

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (kind == "Element" && pending != null)
                foreach (var kv in pending) values[kv.Key] = kv.Value;
            if (related is ProjectInfo info)
            {
                values["Name"] = info.Name;
                values["Number"] = info.Number;
            }
            else if (!values.ContainsKey("Name")) values["Name"] = related.Name;

            foreach (Parameter p in related.Parameters)
            {
                string name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name) || values.ContainsKey(name)) continue;
                try { values[name] = TemplateValue(p); }
                catch { /* unreadable parameter: left out */ }
            }
            return values;
        }

        private static object TemplateValue(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Double:
                case StorageType.Integer:
                    string shown = p.AsValueString();
                    if (double.TryParse(shown, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                        return d;
                    return shown ?? (p.StorageType == StorageType.Integer ? (object)(double)p.AsInteger() : p.AsDouble());
                default: return p.AsValueString();
            }
        }

        private string Resolve(Element el, string token, IDictionary<string, string> pending)
        {
            if (token.IndexOf('|') >= 0)
            {
                foreach (var alt in IdPattern.Alternatives(token))
                {
                    string v = Resolve(el, alt, pending);
                    if (v != null) return v;
                }
                return null;
            }
            try
            {
                if (token == "CategoryPrefix") return _rules.PrefixFor(el.Category?.Name);

                if (token == "Workset")
                {
                    if (!_doc.IsWorkshared) return null;
                    return Clean(_doc.GetWorksetTable().GetWorkset(el.WorksetId)?.Name);
                }

                int dot = token.IndexOf('.');
                if (dot < 0) return null;
                if (pending != null && token.StartsWith("Element.", StringComparison.Ordinal) &&
                    pending.TryGetValue(token.Substring(dot + 1), out var next))
                    return Clean(next);
                Element related = Related(el, token.Substring(0, dot));
                return related == null ? null : ParamText(related, token.Substring(dot + 1));
            }
            catch
            {
                return null;
            }
        }

        private Element Related(Element el, string kind)
        {
            switch (kind)
            {
                case "Level": return LevelOf(el);
                case "Host": return (el as FamilyInstance)?.Host;
                case "Type": return Get(el.GetTypeId());
                case "Group": return Get(el.GroupId);
                case "Room": return (el as FamilyInstance)?.Room ?? RoomAt(el);
                case "Space": return (el as FamilyInstance)?.Space ?? SpaceAt(el);
                // Doors / windows: the rooms on either side (final phase). Null for other elements.
                case "ToRoom": return (el as FamilyInstance)?.ToRoom;
                case "FromRoom": return (el as FamilyInstance)?.FromRoom;
                case "Element": return el;
                case "Project": return _doc.ProjectInformation;
                default: return null;
            }
        }

        private Element Get(ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? null : _doc.GetElement(id);

        private Element LevelOf(Element el)
        {
            Element level = Get(el.LevelId);
            if (level != null) return level;

            // Hosted/level-based elements keep their level in a parameter rather than Element.LevelId.
            foreach (var bip in new[]
            {
                BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM,
                BuiltInParameter.WALL_BASE_CONSTRAINT,
            })
            {
                var p = el.get_Parameter(bip);
                if (p != null && p.StorageType == StorageType.ElementId)
                {
                    level = Get(p.AsElementId());
                    if (level != null) return level;
                }
            }
            return null;
        }

        private XYZ PointOf(Element el)
        {
            XYZ pt = null;
            if (el.Location is LocationPoint lp) pt = lp.Point;
            else if (el.Location is LocationCurve lc) pt = lc.Curve.Evaluate(0.5, true);
            // Nudge up so a point on the floor finishes inside the room volume, not on its boundary.
            return pt == null ? null : new XYZ(pt.X, pt.Y, pt.Z + 0.1);
        }

        private Element RoomAt(Element el)
        {
            var pt = PointOf(el);
            return pt == null ? null : _doc.GetRoomAtPoint(pt);
        }

        private Element SpaceAt(Element el)
        {
            var pt = PointOf(el);
            return pt == null ? null : _doc.GetSpaceAtPoint(pt);
        }

        private static string ParamText(Element related, string param)
        {
            if (related is ProjectInfo info)
            {
                // ProjectInfo.Name is "Project Information"; {Project.Name} / {Project.Number} mean the project's.
                if (string.Equals(param, "Name", StringComparison.OrdinalIgnoreCase)) return Clean(info.Name);
                if (string.Equals(param, "Number", StringComparison.OrdinalIgnoreCase)) return Clean(info.Number);
            }
            if (string.Equals(param, "Name", StringComparison.OrdinalIgnoreCase)) return Clean(related.Name);

            var p = related.LookupParameter(param);
            if (p == null) return null;
            string text = p.StorageType == StorageType.String ? p.AsString() : (p.AsValueString() ?? p.AsString());
            return Clean(text);
        }

        private static string Clean(string s)
        {
            s = s?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
