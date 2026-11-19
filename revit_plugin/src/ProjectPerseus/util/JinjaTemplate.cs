using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Jinja2-style templates for Auto Parameters value rules, rendered by Scriban in Liquid mode (the closest syntax to
    /// Jinja2) after a small translation of the Jinja spellings Liquid lacks. Pure (no Revit); tested in IdPatternTest.cs.
    ///
    ///   {{ Element.Door_Tag_Room_Number }}        parameter of the element; spaces and other characters that can't be in a
    ///   {{ Type.Tag_Template }}                   name become "_" (SafeName). Also lower case ({{ type.tag_template }}) and
    ///   {{ Element["Door Tag Room Number"] }}     the exact name in brackets.
    ///   Objects: Element, Type, Level, Host, Room, Space, ToRoom, FromRoom, Group, Project; values CategoryPrefix, Workset.
    ///   Chains up to 3 steps: {{ Host.Room.Name }}, {{ Host.ToRoom.Number }}, {{ Host.Level.Name }}. An object on its own
    ///   prints its name ({{ Host.Level }} = the host's level name). A relation name wins over a parameter of the same name
    ///   (Element.Level = the level object), falling back to the parameter when there is no such object.
    ///   {% if Element.Width > 900 %}W{% elif ... %}..{% else %}..{% endif %}, {% for %}..{% endfor %}, {% set x = .. %},
    ///   {# comment #}, filters {{ x | upper }}, {{ x | default('-') }}, {{ x | replace('a', 'b') }}, lower, title, trim,
    ///   length, int, float, round(n), plus Liquid's own (upcase, append, slice...).
    /// A missing object or parameter renders as nothing. Numbers (length, area, integers) are numbers, so comparisons work.
    /// </summary>
    public sealed class JinjaTemplate
    {
        /// <summary>The related objects a template can read, as written in templates.</summary>
        public static readonly string[] ObjectNames =
            { "Element", "Type", "Level", "Host", "Room", "Space", "ToRoom", "FromRoom", "Group", "Project" };

        /// <summary>How many steps a chain may take (Host.Room.Level).</summary>
        public const int MaxDepth = 3;

        /// <summary>Where a template's objects come from: the Revit side (RelatedValueResolver) or a test fake.</summary>
        public interface IObjectSource
        {
            /// <summary>The object related to <paramref name="from"/> by kind ("Host", "Room"...); null when none.</summary>
            object Related(object from, string kind);
            /// <summary>An object's parameters by their real names ("Name" included).</summary>
            IDictionary<string, object> Parameters(object obj);
        }

        private static readonly string Kinds = string.Join("|", ObjectNames);
        private static readonly Regex ObjectPath = new Regex(
            @"\b(" + Kinds + @")((?:\.(?:" + Kinds + @")\b)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Template _template;

        /// <summary>Objects the template starts from (canonical spelling), e.g. "Type", "Host".</summary>
        public IReadOnlyList<string> Objects { get; }

        /// <summary>Object chains the template mentions, e.g. ["Host", "Room"] for {{ Host.Room.Name }}.</summary>
        public IReadOnlyList<string[]> Paths { get; }

        private JinjaTemplate(Template template, List<string[]> paths)
        {
            _template = template;
            Paths = paths;
            Objects = paths.Select(x => x[0]).Distinct().ToList();
        }

        private static string Canonical(string kind) =>
            ObjectNames.FirstOrDefault(n => string.Equals(n, kind, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when text is a Jinja template ({{ }}, {% %} or {# #}); single-brace text is a Perseus pattern.</summary>
        public static bool IsJinja(string text) =>
            !string.IsNullOrEmpty(text) && (text.Contains("{{") || text.Contains("{%") || text.Contains("{#"));

        /// <summary>
        /// A parameter name as a template name: anything but letters, digits and "_" becomes "_", runs collapse, ends trimmed,
        /// a leading digit gets "_". "Door Tag Room Number" -> Door_Tag_Room_Number, "Fire Rating (mins)" -> Fire_Rating_mins.
        /// </summary>
        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool under = false;
            foreach (char c in name.Trim())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); under = false; }
                else if (!under) { sb.Append('_'); under = true; }
            }
            string s = sb.ToString().Trim('_');
            return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
        }

        private static readonly Regex Comment = new Regex(@"\{#.*?#\}", RegexOptions.Singleline);
        private static readonly Regex Tag = new Regex(@"(\{\{-?|\{%-?)(.*?)(-?\}\}|-?%\})", RegexOptions.Singleline);
        private static readonly Regex FilterCall = new Regex(@"\|\s*([A-Za-z_]\w*)\s*\(([^()]*)\)");

        /// <summary>Jinja spellings -> Liquid: comments dropped, elif -> elsif, set -> assign, | f(a, b) -> | f: a, b.</summary>
        public static string ToLiquid(string jinja)
        {
            string s = Comment.Replace(jinja ?? "", "");
            return Tag.Replace(s, m =>
            {
                string body = m.Groups[2].Value;
                if (m.Groups[1].Value.StartsWith("{%", StringComparison.Ordinal))
                {
                    body = Regex.Replace(body, @"^(\s*)elif\b", "$1elsif");
                    body = Regex.Replace(body, @"^(\s*)set\b", "$1assign");
                }
                body = FilterCall.Replace(body, f =>
                    f.Groups[2].Value.Trim().Length == 0 ? "| " + f.Groups[1].Value : "| " + f.Groups[1].Value + ": " + f.Groups[2].Value.Trim());
                return m.Groups[1].Value + body + m.Groups[3].Value;
            });
        }

        /// <summary>Parses a template; null with <paramref name="error"/> when it isn't valid.</summary>
        public static JinjaTemplate Parse(string text, out string error)
        {
            error = null;
            try
            {
                var t = Template.ParseLiquid(ToLiquid(text));
                if (t.HasErrors)
                {
                    error = string.Join("; ", t.Messages.Select(m => m.Message)).Trim();
                    return null;
                }
                var paths = new List<string[]>();
                foreach (Match m in ObjectPath.Matches(text))
                {
                    var path = m.Value.Split('.').Select(Canonical).Take(MaxDepth).ToArray();
                    if (!paths.Any(x => x.SequenceEqual(path))) paths.Add(path);
                }
                return new JinjaTemplate(t, paths);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Renders for one element. <paramref name="source"/> supplies the objects (asked only for those the template
        /// reaches, chains followed on demand); <paramref name="element"/> = the element being filled in.
        /// <paramref name="values"/> = top-level values (CategoryPrefix, Workset). Result trimmed.
        /// </summary>
        public bool TryRender(IObjectSource source, object element, IDictionary<string, object> values,
                              out string result, out string error)
        {
            result = "";
            error = null;
            try
            {
                var globals = new ScriptObject();
                AddFilters(globals);
                if (values != null)
                    foreach (var kv in values) Put(globals, kv.Key, kv.Value);
                foreach (var name in Objects)
                {
                    var handle = source.Related(element, name);
                    if (handle != null) Put(globals, name, new LazyObject(source, handle, 1));
                }
                var context = new Context { StrictVariables = false, LoopLimit = 1000, RecursiveLimit = 50 };
                context.PushGlobal(globals);
                result = (_template.Render(context) ?? "").Trim();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// An object's parameters, with its relations (Room, Level...) looked up only when a template asks for them.
        /// </summary>
        private sealed class LazyObject : ScriptObject
        {
            private readonly IObjectSource _source;
            private readonly object _handle;
            private readonly int _depth;
            private readonly Dictionary<string, LazyObject> _related = new Dictionary<string, LazyObject>();
            public readonly string Display;

            public LazyObject(IObjectSource source, object handle, int depth)
            {
                _source = source;
                _handle = handle;
                _depth = depth;
                var parameters = source.Parameters(handle);
                if (parameters == null) return;
                foreach (var kv in parameters) Put(this, kv.Key, kv.Value);
                Display = parameters.TryGetValue("Name", out var n) ? Str(n) : "";
            }

            public override bool TryGetValue(TemplateContext context, SourceSpan span, string member, out object value)
            {
                string kind = Canonical(member);
                if (kind != null && _depth < MaxDepth)
                {
                    if (!_related.TryGetValue(kind, out var rel))
                    {
                        var h = _source.Related(_handle, kind);
                        _related[kind] = rel = h == null ? null : new LazyObject(_source, h, _depth + 1);
                    }
                    if (rel != null) { value = rel; return true; }
                }
                return base.TryGetValue(context, span, member, out value); // a parameter of that name, if any
            }
        }

        // An object on its own prints its name.
        private sealed class Context : LiquidTemplateContext
        {
            public override string ObjectToString(object value, bool nested = false) =>
                value is LazyObject o ? o.Display ?? "" : base.ObjectToString(value, nested);
        }

        // Name as given, its SafeName and the SafeName in lower case; the first parameter to claim a name keeps it.
        private static void Put(ScriptObject obj, string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var key in new[] { name, SafeName(name), SafeName(name).ToLowerInvariant() })
                if (key.Length > 0 && !obj.ContainsKey(key)) obj[key] = value;
        }

        // Jinja filter names Liquid doesn't have.
        private static void AddFilters(ScriptObject o)
        {
            o.Import("upper", new Func<object, string>(v => Str(v).ToUpperInvariant()));
            o.Import("lower", new Func<object, string>(v => Str(v).ToLowerInvariant()));
            o.Import("title", new Func<object, string>(v => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Str(v).ToLowerInvariant())));
            o.Import("trim", new Func<object, string>(v => Str(v).Trim()));
            o.Import("length", new Func<object, int>(v => v is System.Collections.ICollection c ? c.Count : Str(v).Length));
            o.Import("int", new Func<object, long>(v => (long)Math.Truncate(Num(v))));
            o.Import("float", new Func<object, double>(Num));
            o.Import("string", new Func<object, string>(Str));
        }

        private static string Str(object v) =>
            v == null ? "" : v is double d ? d.ToString("0.######", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);

        private static double Num(object v)
        {
            if (v == null) return 0;
            if (v is double d) return d;
            if (v is IConvertible c && !(v is string)) return c.ToDouble(CultureInfo.InvariantCulture);
            var m = Regex.Match(Str(v), @"-?\d+(\.\d+)?");
            return m.Success ? double.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
        }
    }
}
