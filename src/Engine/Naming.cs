using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XtreamFS.Engine
{
    /// <summary>
    /// Title and folder naming. These mirror xtreamfs (Python) exactly, so the
    /// plugin and the container produce the same folder and file names.
    /// </summary>
    public static class Naming
    {
        public const int NamingVersion = 2;

        static readonly Regex PrefixRe = new Regex(
            @"^\s*(?:\|[^|]{1,12}\|\s*|\[[^\]]{1,12}\]\s*|[A-Z0-9]{2,5}\s*[-:|]\s+|(?:4K|UHD)\s+)+",
            RegexOptions.Compiled);
        public static readonly Regex QualityRe = new Regex(@"^\s*(?:4K|UHD)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex YearRe = new Regex(@"[\(\[]?\b(19\d{2}|20\d{2})\b[\)\]]?", RegexOptions.Compiled);
        static readonly Regex YearAny = new Regex(@"(19\d{2}|20\d{2})", RegexOptions.Compiled);
        static readonly Regex BadChars = new Regex("[\\\\/:*?\"<>|\\x00-\\x1f]", RegexOptions.Compiled);
        static readonly Regex MultiSpace = new Regex(@"\s{2,}", RegexOptions.Compiled);
        static readonly Regex ColonSpace = new Regex(@"\s*:\s+", RegexOptions.Compiled);
        static readonly Regex ExtRe = new Regex("^[a-z0-9]{2,4}$", RegexOptions.Compiled);

        public static string CleanTitle(object name, bool stripPrefix)
        {
            var s = Json.S(name).Trim();
            if (stripPrefix) s = PrefixRe.Replace(s, "", 1);
            return s.Trim();
        }

        public static (string title, string year) SplitYear(string title, object yearField)
        {
            string year = null;
            if (Json.Truthy(yearField))
            {
                var m0 = YearAny.Match(Json.S(yearField));
                if (m0.Success) year = m0.Groups[1].Value;
            }
            Match last = null;
            foreach (Match m in YearRe.Matches(title)) last = m;
            if (last != null)
            {
                year = year ?? last.Groups[1].Value;
                title = title.Substring(0, last.Index) + title.Substring(last.Index + last.Length);
            }
            title = MultiSpace.Replace(title, " ").Trim(' ', '-', '.', '_');
            return (title, year);
        }

        public static string Safe(string s)
        {
            s = ColonSpace.Replace(s ?? "", " - ");
            s = BadChars.Replace(s, " ");
            s = MultiSpace.Replace(s, " ").Trim(' ', '.');
            return s.Length > 0 ? s : "Unknown";
        }

        /// <summary>Providers use either "tmdb_id" or "tmdb".</summary>
        public static object TmdbOf(object item) => Json.Or(Json.Get(item, "tmdb_id"), Json.Get(item, "tmdb"));

        public static bool IsDigits(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            foreach (var c in v) if (!char.IsDigit(c)) return false;
            return true;
        }

        public static string TmdbTag(object v)
        {
            var s = Json.S(v).Trim();
            return IsDigits(s) && s != "0" ? " {tmdb-" + s + "}" : "";
        }

        public static string ExtOf(object v, string dflt = "mkv")
        {
            var s = Json.S(v).Trim().TrimStart('.').ToLowerInvariant();
            return ExtRe.IsMatch(s) ? s : dflt;
        }

        public static long ToInt(object v, long dflt = 0)
        {
            if (v is bool b) return b ? 1 : 0;
            var s = Json.S(v).Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && !double.IsNaN(d) && !double.IsInfinity(d))
                return (long)Math.Truncate(d);
            return dflt;
        }

        /// <summary>Unix timestamp from the API, ignoring missing or nonsense values.</summary>
        public static long ToTs(object v, long dflt)
        {
            var t = ToInt(v, 0);
            return t > 946684800 ? t : dflt; // after 2000-01-01
        }

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
