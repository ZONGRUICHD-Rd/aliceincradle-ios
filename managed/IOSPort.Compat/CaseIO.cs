using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace IOSPort.Compat
{
    /// <summary>
    /// The game was written for the case-insensitive Windows file system (and
    /// sometimes builds paths with backslashes), while the iOS file system is
    /// case-sensitive. When a path does not exist as spelled, look it up
    /// component by component in the real directory listings.
    /// </summary>
    public static class CaseIO
    {
        static readonly object gate = new object();
        // listings of directories inside the (read-only) app bundle
        static readonly Dictionary<string, Dictionary<string, string>> listings =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>The existing path matching <paramref name="path"/> ignoring case, or the path itself.</summary>
        public static string Resolve(string path)
        {
            return TryResolve(path, out string found) ? found : path;
        }

        /// <summary>For files that may be created: resolve the file, or else its directory.</summary>
        public static string ResolveForCreate(string path)
        {
            if (TryResolve(path, out string found)) return found;
            try
            {
                string p = path.Replace('\\', '/');
                string dir = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(dir) || !TryResolve(dir, out string realDir)) return path;
                return Path.Combine(realDir, Path.GetFileName(p));
            }
            catch (Exception)
            {
                return path;
            }
        }

        public static bool TryResolve(string path, out string found)
        {
            found = path;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) return true;
                string p = path.IndexOf('\\') >= 0 ? path.Replace('\\', '/') : path;
                if (p != path && (File.Exists(p) || Directory.Exists(p)))
                {
                    found = p;
                    return true;
                }
                string full = Path.GetFullPath(p);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                string cur = root;
                foreach (var part in full.Substring(root.Length).Split(new[] { '/', Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string next = Path.Combine(cur, part);
                    if (File.Exists(next) || Directory.Exists(next))
                    {
                        cur = next;
                        continue;
                    }
                    string real = FindEntry(cur, part);
                    if (real == null) return false;
                    cur = Path.Combine(cur, real);
                }
                found = cur;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string FindEntry(string dir, string name)
        {
            Dictionary<string, string> map = null;
            bool cacheable = IsInAppBundle(dir);
            if (cacheable)
                lock (gate) listings.TryGetValue(dir, out map);
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(dir))
                    foreach (var entry in Directory.GetFileSystemEntries(dir))
                    {
                        string n = Path.GetFileName(entry);
                        if (!map.ContainsKey(n)) map.Add(n, n);
                    }
                if (cacheable)
                    lock (gate) listings[dir] = map;
            }
            return map.TryGetValue(name, out string real) ? real : null;
        }

        // the app bundle never changes; Documents and caches can, so they are listed each time
        static bool IsInAppBundle(string dir) =>
            dir.IndexOf(".app/", StringComparison.Ordinal) >= 0 || dir.EndsWith(".app", StringComparison.Ordinal);

        /// <summary>Directory.GetFiles with a case-insensitive directory and pattern.</summary>
        public static string[] GetFiles(string path, string searchPattern, SearchOption option)
        {
            string dir = Resolve(path);
            if (string.IsNullOrEmpty(searchPattern) || !HasLetter(searchPattern))
                return Directory.GetFiles(dir, string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern, option);
            var rx = WildcardToRegex(searchPattern);
            var result = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "*", option))
                if (rx.IsMatch(Path.GetFileName(f))) result.Add(f);
            return result.ToArray();
        }

        static bool HasLetter(string s)
        {
            foreach (char c in s)
                if (char.IsLetter(c)) return true;
            return false;
        }

        static Regex WildcardToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char c in pattern)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
