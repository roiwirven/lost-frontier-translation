// Translation engine (no Unity code): loads the translations folder, looks texts up, folds accents.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LostFrontierTranslation
{
    public class TranslationCore
    {
        public class Lang
        {
            public string Code, Name;
            public Dictionary<string, string> Dict = new Dictionary<string, string>();
            public List<KeyValuePair<Regex, string>> Patterns = new List<KeyValuePair<Regex, string>>();
        }

        public readonly List<Lang> Langs = new List<Lang>();
        public Lang Current;                                   // null = English (original)
        readonly Dictionary<string, string> reverse = new Dictionary<string, string>();
        readonly HashSet<string> englishKeys = new HashSet<string>();
        public Action<string> Warn = delegate { };
        public Action<string> Missing = delegate { };

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n'); else if (n == 't') sb.Append('\t'); else if (n == 'r') sb.Append('\r'); else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        /// Loads every *.tsv file of the "translations" folder. Each file holds one kind of game data,
        /// with the same columns:  en  <lang1>  <lang2> ...   (any column after "en" is a language)
        /// How a file is used depends on its name:
        ///   languages.tsv = one row: the name of each language, shown in Options > Language
        ///   dynamic.tsv   = texts built by the game at runtime: "en" is a .NET regular expression,
        ///                   translations may use {1}.. (captured value) and {T1}.. (captured value, translated)
        ///   any other     = exact game texts (en) and their translations
        /// Cells use \n for new lines, \t for tabs, \\ for a backslash. Empty cell = not translated.
        public string Load(string folder)
        {
            if (!Directory.Exists(folder)) { Warn("Translation folder not found: " + folder); return "none"; }
            var files = new List<string>(Directory.GetFiles(folder, "*.tsv"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) LoadFile(f, Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
            var report = new StringBuilder();
            foreach (var L in Langs) report.Append(L.Code).Append('(').Append(L.Dict.Count).Append('+').Append(L.Patterns.Count).Append("r) ");
            return report.ToString();
        }

        Lang GetLang(string code)
        {
            var L = Langs.Find(x => x.Code == code);
            if (L == null) { L = new Lang { Code = code, Name = code }; Langs.Add(L); }
            return L;
        }

        void LoadFile(string path, string kind)
        {
            string[] header = null; int enCol = -1; var cols = new Lang[0];
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
                if (line.Length == 0 || line[0] == '#') continue;
                var cells = line.Split('\t');
                if (header == null)
                {
                    header = cells;
                    enCol = Array.IndexOf(header, "en");
                    if (enCol < 0) { Warn(Path.GetFileName(path) + ": no 'en' column"); return; }
                    cols = new Lang[header.Length];
                    for (int c = enCol + 1; c < header.Length; c++)
                        if (header[c].Trim().Length > 0) cols[c] = GetLang(header[c].Trim().ToLowerInvariant());
                    continue;
                }
                if (cells.Length <= enCol) continue;
                string en = cells[enCol];
                string src = Unescape(en);
                for (int c = enCol + 1; c < cells.Length && c < cols.Length; c++)
                {
                    var L = cols[c];
                    if (L == null) continue;
                    string val = Unescape(cells[c]);
                    if (val.Length == 0) continue;
                    if (kind == "languages") { L.Name = val; continue; }
                    if (kind == "dynamic")
                    {
                        try { L.Patterns.Add(new KeyValuePair<Regex, string>(new Regex(en, RegexOptions.Singleline), val)); }
                        catch (Exception e) { Warn("dynamic.tsv: bad regex " + en + ": " + e.Message); break; }
                        continue;
                    }
                    L.Dict[src] = val;
                    englishKeys.Add(src);
                    if (!reverse.ContainsKey(val)) reverse[val] = src;
                }
            }
        }

        public bool SetLanguage(string code)
        {
            code = (code ?? "en").Trim().ToLowerInvariant();
            Current = null;
            foreach (var l in Langs) if (l.Code == code) Current = l;
            return Current != null || code == "en";
        }

        public string NextCode()
        {
            int idx = Current == null ? -1 : Langs.IndexOf(Current);
            idx++;
            return idx >= Langs.Count ? "en" : Langs[idx].Code;
        }

        public string CurrentCode { get { return Current == null ? "en" : Current.Code; } }
        public string CurrentName { get { return Current == null ? "English (original)" : Current.Name; } }

        /// Maps a string that may already be a translation (from any language) back to English.
        public string ToEnglish(string s)
        {
            if (s == null || englishKeys.Contains(s)) return s;
            string back;
            return reverse.TryGetValue(s, out back) ? back : s;
        }

        public static bool IsTrivial(string s)
        {
            int letters = 0;
            foreach (char c in s) if (char.IsLetter(c)) { letters++; if (letters >= 2) return false; }
            return true;
        }

        public string Translate(string s)
        {
            if (Current == null || string.IsNullOrEmpty(s)) return s;
            string r;
            if (Current.Dict.TryGetValue(s, out r)) return r;

            int a = 0, b = s.Length;
            while (a < b && char.IsWhiteSpace(s[a])) a++;
            while (b > a && char.IsWhiteSpace(s[b - 1])) b--;
            if (b - a <= 0) return s;
            string core = (a > 0 || b < s.Length) ? s.Substring(a, b - a) : s;
            if (!ReferenceEquals(core, s) && Current.Dict.TryGetValue(core, out r)) return s.Substring(0, a) + r + s.Substring(b);

            foreach (var p in Current.Patterns)
            {
                var m = p.Key.Match(core);
                if (!m.Success) continue;
                string outp = p.Value;
                for (int g = m.Groups.Count - 1; g >= 1; g--)
                {
                    string gv = m.Groups[g].Value;
                    outp = outp.Replace("{T" + g + "}", Lookup(gv)).Replace("{" + g + "}", gv);
                }
                return s.Substring(0, a) + outp + s.Substring(b);
            }
            if (!IsTrivial(s)) Missing(s);
            return s;
        }

        string Lookup(string s)
        {
            string r;
            if (Current != null && Current.Dict.TryGetValue(s, out r)) return r;
            return s;
        }

        // ---------------------------------------------------------------- glyph folding
        static readonly Dictionary<char, string> fold = BuildFold();
        static Dictionary<char, string> BuildFold()
        {
            var d = new Dictionary<char, string>();
            string[] map = {
                "àáâãäåā", "a", "ÀÁÂÃÄÅĀ", "A", "çćč", "c", "ÇĆČ", "C", "èéêëēę", "e", "ÈÉÊËĒĘ", "E",
                "ìíîïī", "i", "ÌÍÎÏĪ", "I", "ñń", "n", "ÑŃ", "N", "òóôõöøō", "o", "ÒÓÔÕÖØŌ", "O",
                "ùúûüū", "u", "ÙÚÛÜŪ", "U", "ýÿ", "y", "ÝŸ", "Y", "ß", "ss", "œ", "oe", "Œ", "OE", "æ", "ae", "Æ", "AE",
                "«»“”„", "\"", "‘’‚", "'", "–—", "-", "¡¿", "", "…", "...", "\u00A0\u202F", " " };
            for (int i = 0; i < map.Length; i += 2) foreach (char c in map[i]) d[c] = map[i + 1];
            return d;
        }

        /// strip characters the font cannot draw. has(c)==false -> folded
        public static string FoldGlyphs(string s, Func<char, bool> has, bool always)
        {
            if (string.IsNullOrEmpty(s)) return s;
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string rep;
                if (c > 127 && fold.TryGetValue(c, out rep) && (always || !has(c)))
                {
                    if (sb == null) { sb = new StringBuilder(s.Length + 8); sb.Append(s, 0, i); }
                    sb.Append(rep);
                }
                else if (sb != null) sb.Append(c);
            }
            return sb == null ? s : sb.ToString();
        }

        public static int VisibleLen(string s)
        {
            if (s == null) return 0;
            int n = 0;
            for (int i = 0; i < s.Length; i++) { if (s[i] == '@' && i + 1 < s.Length) { i++; continue; } n++; }
            return n;
        }
    }
}
