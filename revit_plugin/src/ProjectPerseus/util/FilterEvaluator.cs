using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ProjectPerseus.logging;

namespace ProjectPerseus.util
{
    // Evaluates Python-subset expressions against a key-schedule row.
    //
    // Supported syntax:
    //   row["Col"] / row['Col']           — column value (always a string)
    //   row.get("Col") / row.get("Col","default")
    //   "x" in row["Col"]                 — substring test (case-sensitive)
    //   "x" not in row["Col"]
    //   row["Col"].startswith("x")        — string methods (case-sensitive)
    //   row["Col"].endswith("x")
    //   row["Col"].lower() / .upper() / .strip() / .lstrip() / .rstrip()
    //   row["Col"].replace("a","b")
    //   re.search(r"pattern", row["Col"]) — regex (True/False)
    //   re.match(r"pattern", row["Col"])  — anchored at start
    //   re.fullmatch(r"pattern", row["Col"])
    //   re.findall(r"pattern", row["Col"])— truthy if ≥1 match
    //   math.sqrt(x) / math.floor(x) / math.ceil(x) / math.log(x)
    //   math.pow(x,y) / math.fabs(x) / math.isnan(x) / math.isinf(x)
    //   math.pi / math.e
    //   len(x), float(x), int(x), str(x), bool(x), abs(x), round(x)
    //   == != < > <= >=   and / or / not   + - * / %
    //   True / False / None
    //
    // On any parse or runtime error, returns false and logs a warning.
    internal static class FilterEvaluator
    {
        public static bool Evaluate(string expression, Dictionary<string, string> row)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;
            try
            {
                var p = new Parser(expression.Trim(), row);
                object result = p.ParseOr();
                p.ExpectEnd();
                return Truthy(result);
            }
            catch (Exception ex)
            {
                Log.Warn($"[FilterEvaluator] '{expression}': {ex.Message}");
                return false;
            }
        }

        // ── Truthiness (Python semantics) ────────────────────────────────────

        internal static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool b) return b;
            if (v is double d) return d != 0.0;
            if (v is string s) return s.Length > 0;
            if (v is List<string> l) return l.Count > 0;
            return true;
        }

        // ── Tokens ───────────────────────────────────────────────────────────

        private enum TT
        {
            Str, Num, Ident,
            LBracket, RBracket, LParen, RParen,
            Dot, Comma,
            Plus, Minus, Star, Slash, Percent,
            Eq, Neq, Lt, Gt, Le, Ge,
            EOF
        }

        private sealed class Tok
        {
            public TT   Type;
            public string Text;
            public double Num;
            public string Str; // set for TT.Str tokens
        }

        private static List<Tok> Tokenize(string src)
        {
            var toks = new List<Tok>();
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                // --- String literals ('...', "...", r'...', r"...") -----------
                bool raw = c == 'r' && i + 1 < src.Length && (src[i + 1] == '\'' || src[i + 1] == '"');
                if (raw) { i++; c = src[i]; }
                if (c == '\'' || c == '"')
                {
                    char q = c; i++;
                    var sb = new StringBuilder();
                    while (i < src.Length && src[i] != q)
                    {
                        if (!raw && src[i] == '\\' && i + 1 < src.Length)
                        {
                            i++;
                            switch (src[i])
                            {
                                case 'n':  sb.Append('\n'); break;
                                case 't':  sb.Append('\t'); break;
                                case '\\': sb.Append('\\'); break;
                                case '\'': sb.Append('\''); break;
                                case '"':  sb.Append('"');  break;
                                default:   sb.Append('\\'); sb.Append(src[i]); break;
                            }
                        }
                        else sb.Append(src[i]);
                        i++;
                    }
                    if (i < src.Length) i++; // closing quote
                    string sv = sb.ToString();
                    toks.Add(new Tok { Type = TT.Str, Text = sv, Str = sv });
                    continue;
                }

                // --- Numbers --------------------------------------------------
                if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
                {
                    int start = i;
                    while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;
                    if (i < src.Length && (src[i] == 'e' || src[i] == 'E'))
                    {
                        i++;
                        if (i < src.Length && (src[i] == '+' || src[i] == '-')) i++;
                        while (i < src.Length && char.IsDigit(src[i])) i++;
                    }
                    string ns = src.Substring(start, i - start);
                    double.TryParse(ns, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double nv);
                    toks.Add(new Tok { Type = TT.Num, Text = ns, Num = nv });
                    continue;
                }

                // --- Identifiers / keywords -----------------------------------
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                    toks.Add(new Tok { Type = TT.Ident, Text = src.Substring(start, i - start) });
                    continue;
                }

                // --- Two-char operators ---------------------------------------
                if (i + 1 < src.Length)
                {
                    string two = src.Substring(i, 2);
                    TT twoType;
                    bool twoMatch = true;
                    if      (two == "==") twoType = TT.Eq;
                    else if (two == "!=") twoType = TT.Neq;
                    else if (two == "<=") twoType = TT.Le;
                    else if (two == ">=") twoType = TT.Ge;
                    else { twoType = TT.EOF; twoMatch = false; }
                    if (twoMatch) { toks.Add(new Tok { Type = twoType, Text = two }); i += 2; continue; }
                }

                // --- Single-char operators ------------------------------------
                TT oneType;
                bool oneMatch = true;
                if      (c == '<') oneType = TT.Lt;
                else if (c == '>') oneType = TT.Gt;
                else if (c == '[') oneType = TT.LBracket;
                else if (c == ']') oneType = TT.RBracket;
                else if (c == '(') oneType = TT.LParen;
                else if (c == ')') oneType = TT.RParen;
                else if (c == '.') oneType = TT.Dot;
                else if (c == ',') oneType = TT.Comma;
                else if (c == '+') oneType = TT.Plus;
                else if (c == '-') oneType = TT.Minus;
                else if (c == '*') oneType = TT.Star;
                else if (c == '/') oneType = TT.Slash;
                else if (c == '%') oneType = TT.Percent;
                else { oneType = TT.EOF; oneMatch = false; }
                if (oneMatch) { toks.Add(new Tok { Type = oneType, Text = c.ToString() }); i++; continue; }

                throw new FormatException($"Unexpected character '{c}'");
            }
            toks.Add(new Tok { Type = TT.EOF, Text = "<end>" });
            return toks;
        }

        // ── Module sentinels ─────────────────────────────────────────────────

        private sealed class ReModule   { public static readonly ReModule   I = new ReModule();   }
        private sealed class MathModule { public static readonly MathModule I = new MathModule(); }

        // ── Parser / evaluator ───────────────────────────────────────────────

        private sealed class Parser
        {
            private readonly List<Tok>                    _t;
            private          int                          _p;
            private readonly Dictionary<string, string>  _row;

            public Parser(string src, Dictionary<string, string> row)
            {
                _t   = Tokenize(src);
                _row = row;
            }

            public void ExpectEnd()
            {
                if (Cur.Type != TT.EOF)
                    throw new FormatException($"Unexpected '{Cur.Text}'");
            }

            private Tok Cur  => _t[_p];
            private Tok Peek => _p + 1 < _t.Count ? _t[_p + 1] : _t[_t.Count - 1];

            private Tok Eat()
            {
                Tok t = _t[_p];
                if (_p < _t.Count - 1) _p++;
                return t;
            }

            private void Expect(TT type, string desc)
            {
                if (Cur.Type != type) throw new FormatException($"Expected {desc}, got '{Cur.Text}'");
                Eat();
            }

            // or_expr := and_expr ('or' and_expr)*
            public object ParseOr()
            {
                object v = ParseAnd();
                while (Cur.Type == TT.Ident && Cur.Text == "or")
                {
                    Eat();
                    object r = ParseAnd();
                    v = Truthy(v) || Truthy(r);
                }
                return v;
            }

            // and_expr := not_expr ('and' not_expr)*
            private object ParseAnd()
            {
                object v = ParseNot();
                while (Cur.Type == TT.Ident && Cur.Text == "and")
                {
                    Eat();
                    object r = ParseNot();
                    v = Truthy(v) && Truthy(r);
                }
                return v;
            }

            // not_expr := 'not' not_expr | compare_expr
            // 'not in' is consumed at the compare level, not here.
            private object ParseNot()
            {
                if (Cur.Type == TT.Ident && Cur.Text == "not" &&
                    !(Peek.Type == TT.Ident && Peek.Text == "in"))
                {
                    Eat();
                    return !Truthy(ParseNot());
                }
                return ParseCompare();
            }

            // compare_expr := add_expr ((comp_op | 'in' | 'not in') add_expr)*
            private object ParseCompare()
            {
                object v = ParseAdd();
                while (true)
                {
                    TT ct = Cur.Type;
                    if (ct == TT.Eq)  { Eat(); v = PyCompare(v, ParseAdd()) == 0;  continue; }
                    if (ct == TT.Neq) { Eat(); v = PyCompare(v, ParseAdd()) != 0;  continue; }
                    if (ct == TT.Lt)  { Eat(); v = PyCompare(v, ParseAdd()) <  0;  continue; }
                    if (ct == TT.Gt)  { Eat(); v = PyCompare(v, ParseAdd()) >  0;  continue; }
                    if (ct == TT.Le)  { Eat(); v = PyCompare(v, ParseAdd()) <= 0;  continue; }
                    if (ct == TT.Ge)  { Eat(); v = PyCompare(v, ParseAdd()) >= 0;  continue; }
                    if (ct == TT.Ident && Cur.Text == "in")
                    {
                        Eat();
                        v = InOp(v, ParseAdd());
                        continue;
                    }
                    if (ct == TT.Ident && Cur.Text == "not" &&
                        Peek.Type == TT.Ident && Peek.Text == "in")
                    {
                        Eat(); Eat();
                        v = !Truthy(InOp(v, ParseAdd()));
                        continue;
                    }
                    break;
                }
                return v;
            }

            private static bool InOp(object needle, object haystack)
            {
                if (haystack is Dictionary<string, string> d)
                    return d.ContainsKey(PyStr(needle));
                return PyStr(haystack).Contains(PyStr(needle));
            }

            // add_expr := mul_expr (('+' | '-') mul_expr)*
            private object ParseAdd()
            {
                object v = ParseMul();
                while (Cur.Type == TT.Plus || Cur.Type == TT.Minus)
                {
                    bool add = Cur.Type == TT.Plus; Eat();
                    object r = ParseMul();
                    if (add && (v is string || r is string))
                        v = PyStr(v) + PyStr(r);
                    else if (add)
                        v = PyNum(v) + PyNum(r);
                    else
                        v = PyNum(v) - PyNum(r);
                }
                return v;
            }

            // mul_expr := unary (('*' | '/' | '%') unary)*
            private object ParseMul()
            {
                object v = ParseUnary();
                while (Cur.Type == TT.Star || Cur.Type == TT.Slash || Cur.Type == TT.Percent)
                {
                    TT op = Cur.Type; Eat();
                    object r = ParseUnary();
                    double rv = PyNum(r);
                    if (op == TT.Star)    v = PyNum(v) * rv;
                    else if (op == TT.Slash)   v = rv == 0 ? throw new DivideByZeroException() : PyNum(v) / rv;
                    else                  v = PyNum(v) % rv;
                }
                return v;
            }

            // unary := '-' unary | postfix
            private object ParseUnary()
            {
                if (Cur.Type == TT.Minus) { Eat(); return -PyNum(ParseUnary()); }
                return ParsePostfix();
            }

            // postfix := primary ('.' IDENT ('(' args ')')? | '[' expr ']')*
            private object ParsePostfix()
            {
                object v = ParsePrimary();
                while (Cur.Type == TT.Dot || Cur.Type == TT.LBracket)
                {
                    if (Cur.Type == TT.Dot)
                    {
                        Eat();
                        if (Cur.Type != TT.Ident)
                            throw new FormatException("Expected attribute name after '.'");
                        string attr = Eat().Text;
                        if (Cur.Type == TT.LParen)
                        {
                            Eat();
                            List<object> args = ParseArgList();
                            Expect(TT.RParen, ")");
                            v = CallMethod(v, attr, args);
                        }
                        else
                        {
                            v = GetAttr(v, attr);
                        }
                    }
                    else // '['
                    {
                        Eat();
                        object idx = ParseOr();
                        Expect(TT.RBracket, "]");
                        v = Subscript(v, idx);
                    }
                }
                return v;
            }

            // primary := literal | '(' expr ')' | IDENT ('(' args ')')?
            private object ParsePrimary()
            {
                Tok t = Cur;
                if (t.Type == TT.Str)   { Eat(); return t.Str; }
                if (t.Type == TT.Num)   { Eat(); return t.Num; }
                if (t.Type == TT.LParen)
                {
                    Eat();
                    object inner = ParseOr();
                    Expect(TT.RParen, ")");
                    return inner;
                }
                if (t.Type == TT.Ident)
                {
                    Eat();
                    switch (t.Text)
                    {
                        case "True":  return (object)true;
                        case "False": return (object)false;
                        case "None":  return null;
                        case "row":   return _row;
                        case "re":    return ReModule.I;
                        case "math":  return MathModule.I;
                    }
                    if (Cur.Type == TT.LParen)
                    {
                        Eat();
                        List<object> args = ParseArgList();
                        Expect(TT.RParen, ")");
                        return CallBuiltin(t.Text, args);
                    }
                    throw new FormatException($"Unknown name '{t.Text}' — did you mean row[\"{t.Text}\"]?");
                }
                throw new FormatException($"Unexpected '{t.Text}'");
            }

            private List<object> ParseArgList()
            {
                var args = new List<object>();
                if (Cur.Type == TT.RParen) return args;
                args.Add(ParseOr());
                while (Cur.Type == TT.Comma) { Eat(); args.Add(ParseOr()); }
                return args;
            }

            // ── Method dispatch ──────────────────────────────────────────────

            private static object CallMethod(object obj, string method, List<object> args)
            {
                if (obj is string s)         return StringMethod(s, method, args);
                if (obj is ReModule)         return ReMethod(method, args);
                if (obj is MathModule)       return MathMethod(method, args);
                if (obj is Dictionary<string, string> d) return DictMethod(d, method, args);
                throw new FormatException($"Cannot call .{method}() on {TypeName(obj)}");
            }

            private static object GetAttr(object obj, string attr)
            {
                if (obj is MathModule)
                {
                    switch (attr)
                    {
                        case "pi":  return Math.PI;
                        case "e":   return Math.E;
                        case "inf": return double.PositiveInfinity;
                        case "nan": return double.NaN;
                    }
                }
                throw new FormatException($"No attribute '{attr}' on {TypeName(obj)}");
            }

            private static object Subscript(object obj, object idx)
            {
                if (obj is Dictionary<string, string> d)
                {
                    string key = PyStr(idx);
                    return d.TryGetValue(key, out string val) ? val : "";
                }
                throw new FormatException($"Cannot subscript {TypeName(obj)}");
            }

            // ── String methods ───────────────────────────────────────────────

            private static object StringMethod(string s, string method, List<object> args)
            {
                string Arg(int n, string def = "") =>
                    args.Count > n ? PyStr(args[n]) : def;

                switch (method)
                {
                    case "startswith": return s.StartsWith(Arg(0), StringComparison.Ordinal);
                    case "endswith":   return s.EndsWith(Arg(0),   StringComparison.Ordinal);
                    case "lower":      return s.ToLowerInvariant();
                    case "upper":      return s.ToUpperInvariant();
                    case "strip":      return s.Trim();
                    case "lstrip":     return s.TrimStart();
                    case "rstrip":     return s.TrimEnd();
                    case "replace":
                        if (args.Count < 2) throw new FormatException("replace() needs 2 args");
                        return s.Replace(Arg(0), Arg(1));
                    case "count":
                    {
                        string sub = Arg(0);
                        if (sub.Length == 0) return (double)(s.Length + 1);
                        int cnt = 0, idx = 0;
                        while ((idx = s.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
                        { cnt++; idx += sub.Length; }
                        return (double)cnt;
                    }
                    case "find":
                        return (double)s.IndexOf(Arg(0), StringComparison.Ordinal);
                    case "split":
                    {
                        var parts = args.Count > 0
                            ? s.Split(new[] { Arg(0) }, StringSplitOptions.None)
                            : s.Split();
                        var list = new List<string>(parts);
                        return list;
                    }
                    default:
                        throw new FormatException($"Unknown string method '{method}'");
                }
            }

            // ── re module methods ────────────────────────────────────────────

            private static object ReMethod(string method, List<object> args)
            {
                if (args.Count < 2)
                    throw new FormatException($"re.{method}() needs (pattern, string)");
                string pattern = PyStr(args[0]);
                string text    = PyStr(args[1]);
                switch (method)
                {
                    case "search":    return Regex.IsMatch(text, pattern);
                    case "match":     return Regex.IsMatch(text, @"\A(?:" + pattern + ")");
                    case "fullmatch": return Regex.IsMatch(text, @"\A(?:" + pattern + @")\z");
                    case "findall":
                    {
                        var ms = Regex.Matches(text, pattern);
                        var list = new List<string>(ms.Count);
                        foreach (Match m in ms) list.Add(m.Value);
                        return list;
                    }
                    default:
                        throw new FormatException($"Unknown re method '{method}'");
                }
            }

            // ── math module methods ──────────────────────────────────────────

            private static object MathMethod(string method, List<object> args)
            {
                if (args.Count < 1) throw new FormatException($"math.{method}() needs at least 1 argument");
                double x = PyNum(args[0]);
                switch (method)
                {
                    case "sqrt":    return Math.Sqrt(x);
                    case "floor":   return Math.Floor(x);
                    case "ceil":    return Math.Ceiling(x);
                    case "fabs":
                    case "abs":     return Math.Abs(x);
                    case "log":     return args.Count > 1 ? Math.Log(x, PyNum(args[1])) : Math.Log(x);
                    case "log10":   return Math.Log10(x);
                    case "log2":    return Math.Log(x, 2);
                    case "exp":     return Math.Exp(x);
                    case "pow":
                        if (args.Count < 2) throw new FormatException("math.pow() needs 2 args");
                        return Math.Pow(x, PyNum(args[1]));
                    case "isnan":   return double.IsNaN(x);
                    case "isinf":   return double.IsInfinity(x);
                    case "isfinite": return !double.IsNaN(x) && !double.IsInfinity(x);
                    case "sin":     return Math.Sin(x);
                    case "cos":     return Math.Cos(x);
                    case "tan":     return Math.Tan(x);
                    default:
                        throw new FormatException($"Unknown math method '{method}'");
                }
            }

            // ── dict methods ─────────────────────────────────────────────────

            private static object DictMethod(Dictionary<string, string> d, string method, List<object> args)
            {
                switch (method)
                {
                    case "get":
                    {
                        if (args.Count < 1) throw new FormatException("row.get() needs at least 1 arg");
                        string key = PyStr(args[0]);
                        string def = args.Count > 1 ? PyStr(args[1]) : "";
                        return d.TryGetValue(key, out string val) ? val : def;
                    }
                    case "keys":   return new List<string>(d.Keys);
                    case "values": return new List<string>(d.Values);
                    default:
                        throw new FormatException($"Unknown dict method '{method}'");
                }
            }

            // ── Built-in functions ───────────────────────────────────────────

            private static object CallBuiltin(string name, List<object> args)
            {
                switch (name)
                {
                    case "len":
                        if (args.Count != 1) throw new FormatException("len() takes 1 argument");
                        if (args[0] is string ls)   return (double)ls.Length;
                        if (args[0] is List<string> ll) return (double)ll.Count;
                        if (args[0] is Dictionary<string, string> ld) return (double)ld.Count;
                        throw new FormatException("len() argument must be a string or collection");
                    case "float":
                        if (args.Count != 1) throw new FormatException("float() takes 1 argument");
                        return PyNum(args[0]);
                    case "int":
                        if (args.Count != 1) throw new FormatException("int() takes 1 argument");
                        return Math.Truncate(PyNum(args[0]));
                    case "str":
                        if (args.Count != 1) throw new FormatException("str() takes 1 argument");
                        return PyStr(args[0]);
                    case "bool":
                        if (args.Count != 1) throw new FormatException("bool() takes 1 argument");
                        return (object)Truthy(args[0]);
                    case "abs":
                        if (args.Count != 1) throw new FormatException("abs() takes 1 argument");
                        return Math.Abs(PyNum(args[0]));
                    case "round":
                        if (args.Count == 1) return Math.Round(PyNum(args[0]));
                        if (args.Count == 2) return Math.Round(PyNum(args[0]), (int)PyNum(args[1]));
                        throw new FormatException("round() takes 1 or 2 arguments");
                    case "min":
                        if (args.Count < 2) throw new FormatException("min() needs ≥2 arguments");
                        { double m = PyNum(args[0]); for (int k = 1; k < args.Count; k++) m = Math.Min(m, PyNum(args[k])); return m; }
                    case "max":
                        if (args.Count < 2) throw new FormatException("max() needs ≥2 arguments");
                        { double m = PyNum(args[0]); for (int k = 1; k < args.Count; k++) m = Math.Max(m, PyNum(args[k])); return m; }
                    default:
                        throw new FormatException($"Unknown function '{name}'");
                }
            }

            // ── Helpers ──────────────────────────────────────────────────────

            private static int PyCompare(object a, object b)
            {
                // Numeric if both are numbers or both look numeric
                if (a is double da && b is double db) return da.CompareTo(db);
                string sa = PyStr(a), sb = PyStr(b);
                if (double.TryParse(sa, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double na) &&
                    double.TryParse(sb, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double nb))
                    return na.CompareTo(nb);
                return string.Compare(sa, sb, StringComparison.Ordinal);
            }

            private static string TypeName(object v)
            {
                if (v == null)                  return "None";
                if (v is string)                return "str";
                if (v is double)                return "float";
                if (v is bool)                  return "bool";
                if (v is ReModule)              return "re";
                if (v is MathModule)            return "math";
                if (v is Dictionary<string, string>) return "dict";
                if (v is List<string>)          return "list";
                return v.GetType().Name;
            }
        }

        // ── Static helpers (shared by Parser and callers) ────────────────────

        internal static string PyStr(object v)
        {
            if (v == null)   return "";
            if (v is bool b) return b ? "True" : "False";
            if (v is double d)
                return d == Math.Truncate(d) ? ((long)d).ToString() : d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return v.ToString();
        }

        internal static double PyNum(object v)
        {
            if (v == null)   return 0.0;
            if (v is double d) return d;
            if (v is bool b)   return b ? 1.0 : 0.0;
            double.TryParse(v.ToString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double r);
            return r;
        }
    }
}
