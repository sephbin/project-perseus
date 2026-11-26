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
    ///
    /// Reference collections (collections/, REFERENCE_COLLECTIONS_PLAN.md part B) are lists named by their collection:
    ///   {% for s in Signs %}{{ s.Message }}{% unless forloop.last %} / {% endunless %}{% endfor %}
    ///   {{ Signs[0].Message }}   {{ Signs | size }}   {{ Signs }} (member names, comma separated)   {{ Type.Signs }}
    /// Each member = the member element's parameters with the owner instance's overrides on top, plus slot (1-based),
    /// id, stale, overridden and base (the values before overrides). A collection name wins over a parameter of the same
    /// name, a relation (Room, Type...) wins over both.
    ///   {{ s.Message | render }} renders a member value that is itself a template for the element being filled in
    ///   (RenderNested): a sign type's Message "Room {{ Room.Number }}" gives each door its own room number.
    ///   A bare name ({{ Mark }}, {{ Messaging_Override }}) = the element's parameter, else its type's (first with a value).
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
            /// <summary>
            /// The members of collection <paramref name="name"/> as seen from <paramref name="from"/> (an owner, or an
            /// instance of an owner type: then with that instance's overrides); null when there is no such collection or
            /// <paramref name="from"/> doesn't own it. Empty slots and unresolvable references are left out.
            /// </summary>
            IList<Member> RelatedMany(object from, string name);
        }

        /// <summary>One member of a reference collection, as the object source supplies it.</summary>
        public sealed class Member
        {
            public object Handle;   // the member element (for Parameters / Related)
            public int Slot;        // 1-based position in the owner's list
            public long Id;
            public bool Stale;      // renamed / found by name only (ReferenceResolver)
            public IDictionary<string, string> Overrides; // field -> value from the owner instance; null = none
        }

        private static readonly string Kinds = string.Join("|", ObjectNames);
        private static readonly Regex ObjectPath = new Regex(
            @"\b(" + Kinds + @")((?:\.(?:" + Kinds + @")\b)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Template _template;
        private readonly string _text;

        /// <summary>Objects the template starts from (canonical spelling), e.g. "Type", "Host".</summary>
        public IReadOnlyList<string> Objects { get; }

        /// <summary>Object chains the template mentions, e.g. ["Host", "Room"] for {{ Host.Room.Name }}.</summary>
        public IReadOnlyList<string[]> Paths { get; }

        private JinjaTemplate(Template template, List<string[]> paths, string text)
        {
            _template = template;
            _text = text ?? "";
            Paths = paths;
            Objects = paths.Select(x => x[0]).Distinct().ToList();
        }

        /// <summary>
        /// The names (e.g. collection names) the template mentions as a whole word, ignoring case. Used for dependencies:
        /// a template naming "Signs" depends on every member of the owner's Signs collection.
        /// </summary>
        public IReadOnlyList<string> Mentions(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n) &&
                            Regex.IsMatch(_text, @"\b" + Regex.Escape(n) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToList();

        private static readonly Regex RenderFilter = new Regex(@"\|\s*render\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when the template renders text that is itself a template ({{ s.Message | render }}). What that text reads
        /// isn't known until render time, so dependency tracking treats every related object as read (AllObjectPaths).
        /// </summary>
        public bool UsesRender => RenderFilter.IsMatch(_text);

        /// <summary>Every object one step from the element (["Type"], ["Room"]...), for templates that use render.</summary>
        public static IReadOnlyList<string[]> AllObjectPaths { get; } = ObjectNames.Select(n => new[] { n }).ToList();

        /// <summary>Just the element's type: bare names fall back to it ({{ Messaging_Override }} = Type.Messaging_Override).</summary>
        public static IReadOnlyList<string[]> TypePath { get; } = new List<string[]> { new[] { "Type" } };

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
                return new JinjaTemplate(t, paths, text);
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
                var globals = new Globals(source, element);
                AddFilters(globals);
                globals.Import("render", new Func<object, string>(v => RenderNested(Str(v), source, element, values)));
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

        [ThreadStatic] private static int _nestedDepth;
        private const int MaxNested = 5;

        /// <summary>
        /// {{ s.Message | render }}: text that is itself a template (e.g. a sign type's Message "Room {{ Room.Number }}")
        /// rendered for the SAME element as the outer template, so Element / Room / Type... mean the element being filled
        /// in, not the member. Plain text comes back as it is. Errors fail the outer render (value not written).
        /// </summary>
        private static string RenderNested(string text, IObjectSource source, object element, IDictionary<string, object> values)
        {
            if (!IsJinja(text)) return text;
            if (_nestedDepth >= MaxNested) throw new InvalidOperationException("render nested more than " + MaxNested + " deep");
            var inner = Parse(text, out string parseError);
            if (inner == null) throw new InvalidOperationException("rendered text '" + text + "' isn't valid: " + parseError);
            _nestedDepth++;
            try
            {
                if (!inner.TryRender(source, element, values, out string result, out string error))
                    throw new InvalidOperationException("rendering '" + text + "' failed: " + error);
                return result;
            }
            finally { _nestedDepth--; }
        }

        /// <summary>Top-level names: values and objects, then the rendered element's collections ({{ Signs }}).</summary>
        private sealed class Globals : ScriptObject
        {
            private readonly IObjectSource _source;
            private readonly object _element;
            private readonly Dictionary<string, MemberList> _collections = new Dictionary<string, MemberList>(StringComparer.OrdinalIgnoreCase);

            public Globals(IObjectSource source, object element)
            {
                _source = source;
                _element = element;
            }

            private LazyObject _self, _type;
            private bool _selfLoaded, _typeLoaded;

            public override bool TryGetValue(TemplateContext context, SourceSpan span, string member, out object value)
            {
                if (base.TryGetValue(context, span, member, out value)) return true;
                var list = Collection(_source, _element, member, 0, _collections);
                if (list != null) { value = list; return true; }
                return BareName(context, span, member, out value);
            }

            // A bare name ({{ Messaging_Override }}) = the element's parameter, else its type's; the first with a value wins.
            private bool BareName(TemplateContext context, SourceSpan span, string member, out object value)
            {
                value = null;
                if (string.IsNullOrEmpty(member)) return false;
                if (!_selfLoaded) { _selfLoaded = true; _self = Load(_element); }
                if (!_typeLoaded) { _typeLoaded = true; _type = Load(_source.Related(_element, "Type")); }
                bool found = false;
                foreach (var obj in new[] { _self, _type })
                {
                    if (obj == null || !obj.TryGetParameter(context, span, member, out var v)) continue;
                    if (!found) { value = v; found = true; }
                    if (v != null && !(v is string s && s.Length == 0)) { value = v; return true; }
                }
                return found;
            }

            private LazyObject Load(object handle) => handle == null ? null : new LazyObject(_source, handle, 1);
        }

        /// <summary>
        /// An object's parameters, with its relations (Room, Level...) and collections looked up only when a template asks
        /// for them.
        /// </summary>
        private class LazyObject : ScriptObject
        {
            protected readonly IObjectSource _source;
            protected readonly object _handle;
            protected readonly int _depth;
            private readonly Dictionary<string, LazyObject> _related = new Dictionary<string, LazyObject>();
            private readonly Dictionary<string, MemberList> _collections = new Dictionary<string, MemberList>(StringComparer.OrdinalIgnoreCase);
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
                if (kind == null && _depth < MaxDepth && member != null && !IsMemberField(member))
                {
                    var list = Collection(_source, _handle, member, _depth, _collections);
                    if (list != null) { value = list; return true; }
                }
                return base.TryGetValue(context, span, member, out value); // a parameter of that name, if any
            }

            protected virtual bool IsMemberField(string member) => false;

            /// <summary>Only a parameter of that name (no relations / collections), for bare-name lookups.</summary>
            public bool TryGetParameter(TemplateContext context, SourceSpan span, string member, out object value) =>
                base.TryGetValue(context, span, member, out value);
        }

        /// <summary>A collection member: the member element with the owner instance's overrides on top.</summary>
        private sealed class MemberObject : LazyObject
        {
            private static readonly string[] Fields = { "slot", "id", "stale", "overridden", "base" };

            public MemberObject(IObjectSource source, Member m, int depth) : base(source, m.Handle, depth)
            {
                var baseValues = new ScriptObject();
                foreach (var key in Keys.ToList()) baseValues[key] = this[key];
                bool overridden = false;
                if (m.Overrides != null)
                    foreach (var kv in m.Overrides)
                    {
                        if (string.IsNullOrEmpty(kv.Key)) continue;
                        object old = Keys.Where(k => string.Equals(k, kv.Key, StringComparison.OrdinalIgnoreCase))
                                         .Select(k => this[k]).FirstOrDefault();
                        Set(this, kv.Key, Typed(kv.Value, old));
                        overridden = true;
                    }
                this["slot"] = m.Slot;
                this["id"] = m.Id;
                this["stale"] = m.Stale;
                this["overridden"] = overridden;
                this["base"] = baseValues;
            }

            protected override bool IsMemberField(string member) =>
                Fields.Contains(member, StringComparer.OrdinalIgnoreCase);

            // An override of a number stays a number when it reads as one (so comparisons keep working).
            private static object Typed(string text, object old)
            {
                if (old is double && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
                return text;
            }
        }

        /// <summary>A collection's members; on its own it prints the member names, comma separated.</summary>
        private sealed class MemberList : ScriptArray
        {
            public override string ToString() =>
                string.Join(", ", this.OfType<LazyObject>().Select(o => o.Display ?? "").Where(s => s.Length > 0));
        }

        private static MemberList Collection(IObjectSource source, object handle, string name, int depth,
                                             Dictionary<string, MemberList> cache)
        {
            if (handle == null || string.IsNullOrEmpty(name)) return null;
            if (cache.TryGetValue(name, out var list)) return list;
            var members = source.RelatedMany(handle, name);
            if (members != null)
            {
                list = new MemberList();
                foreach (var m in members.Where(x => x?.Handle != null).OrderBy(x => x.Slot))
                    list.Add(new MemberObject(source, m, depth + 1));
            }
            cache[name] = list;
            return list;
        }

        // An object on its own prints its name; a collection its member names.
        private sealed class Context : LiquidTemplateContext
        {
            public override string ObjectToString(object value, bool nested = false) =>
                value is LazyObject o ? o.Display ?? "" :
                value is MemberList l ? l.ToString() :
                base.ObjectToString(value, nested);
        }

        // Name as given, its SafeName and the SafeName in lower case; the first parameter to claim a name keeps it.
        private static void Put(ScriptObject obj, string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var key in new[] { name, SafeName(name), SafeName(name).ToLowerInvariant() })
                if (key.Length > 0 && !obj.ContainsKey(key)) obj[key] = value;
        }

        // Like Put, but replaces what is there (overrides), including other spellings already pointing at the old value.
        private static void Set(ScriptObject obj, string name, object value)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal) { name, SafeName(name), SafeName(name).ToLowerInvariant() };
            // Field names in overrides are case-insensitive: "message" overrides the "Message" parameter.
            foreach (var k in obj.Keys.ToList())
                if (keys.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) keys.Add(k);
            foreach (var key in keys)
                if (key.Length > 0) obj[key] = value;
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
