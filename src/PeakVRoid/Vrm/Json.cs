using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PeakVRoid.Vrm
{
    /// <summary>
    /// Tiny dependency-free JSON reader. Objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double, plus string / bool / null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue();
            return v;
        }

        private sealed class Parser
        {
            private readonly string s;
            private int i;

            public Parser(string text) { s = text; }

            public void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ReadValue()
            {
                SkipWs();
                if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException($"Bad JSON token at {i}");
                i += word.Length;
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                i++; // {
                SkipWs();
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    SkipWs();
                    string key = ReadString();
                    SkipWs();
                    if (s[i] != ':') throw new FormatException($"Expected ':' at {i}");
                    i++;
                    d[key] = ReadValue();
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException($"Expected ',' or '}}' at {i}");
                }
            }

            private List<object> ReadArray()
            {
                var l = new List<object>();
                i++; // [
                SkipWs();
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ReadValue());
                    SkipWs();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException($"Expected ',' or ']' at {i}");
                }
            }

            private string ReadString()
            {
                if (s[i] != '"') throw new FormatException($"Expected string at {i}");
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException($"Bad escape at {i}");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException($"Unexpected character '{s[i]}' at {i}");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>Convenience accessors over the loosely-typed JSON tree.</summary>
    public static class JsonExt
    {
        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> Arr(this Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v as List<object> : null;

        public static string Str(this Dictionary<string, object> d, string key, string def = null)
            => d != null && d.TryGetValue(key, out var v) && v is string s ? s : def;

        public static bool Has(this Dictionary<string, object> d, string key)
            => d != null && d.ContainsKey(key) && d[key] != null;

        public static double Num(this Dictionary<string, object> d, string key, double def = 0)
            => d != null && d.TryGetValue(key, out var v) && v is double n ? n : def;

        public static int Int(this Dictionary<string, object> d, string key, int def = -1)
            => d != null && d.TryGetValue(key, out var v) && v is double n ? (int)n : def;

        public static bool Bool(this Dictionary<string, object> d, string key, bool def = false)
            => d != null && d.TryGetValue(key, out var v) && v is bool b ? b : def;

        public static float[] Floats(this Dictionary<string, object> d, string key)
        {
            var a = d.Arr(key);
            if (a == null) return null;
            var r = new float[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = a[i] is double n ? (float)n : 0f;
            return r;
        }

        public static IEnumerable<Dictionary<string, object>> Objs(this List<object> list)
        {
            if (list == null) yield break;
            foreach (var o in list)
                if (o is Dictionary<string, object> d) yield return d;
        }
    }
}
