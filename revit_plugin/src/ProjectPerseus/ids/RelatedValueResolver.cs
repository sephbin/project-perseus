using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using ProjectPerseus.models;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.ids
{
    /// <summary>
    /// Supplies the values for ID pattern tokens: {CategoryPrefix}, {Workset} and
    /// {Level|Host|Room|Space|Type|Group.ParamName}. Returns null when a value is unavailable
    /// (no related element, parameter missing or empty), which makes the caller skip the element.
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

        internal Func<string, string> For(Element el) => token => Resolve(el, token);

        private string Resolve(Element el, string token)
        {
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
