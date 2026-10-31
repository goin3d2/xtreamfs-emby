using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XtreamFS.Engine
{
    public sealed class Entry
    {
        public string Url;
        public string Key;
        public long Mtime;
        public char Kind;                         // 'm' movie, 'e' episode
        public Dictionary<string, object> Item;   // movie or episode record
        public Dictionary<string, object> Show;   // for episodes
    }

    /// <summary>The folder layout: every video path and the folders above it.</summary>
    public sealed class Tree
    {
        public readonly Dictionary<string, HashSet<string>> Dirs =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["/"] = new HashSet<string>() };
        public readonly Dictionary<string, Entry> Files = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public HashSet<string> Dir(string path)
        {
            if (!Dirs.TryGetValue(path, out var set)) Dirs[path] = set = new HashSet<string>();
            return set;
        }

        public void AddFile(string path, Entry entry)
        {
            var parts = path.Trim('/').Split('/');
            var cur = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var parent = cur.Length > 0 ? cur : "/";
                cur = cur + "/" + parts[i];
                Dir(parent).Add(parts[i]);
                Dir(cur);
            }
            Dir(cur.Length > 0 ? cur : "/").Add(parts[parts.Length - 1]);
            Files[path] = entry;
        }

        public string Unique(string path)
        {
            if (!Dirs.ContainsKey(path) && !Files.ContainsKey(path)) return path;
            int i = 2;
            while (Dirs.ContainsKey(path + " (" + i + ")")) i++;
            return path + " (" + i + ")";
        }
    }

    /// <summary>Paths and first-seen times remembered between refreshes.</summary>
    public sealed class BuildState
    {
        public int Naming;
        public string MovieLayout = "flat";
        public string SeriesLayout = "flat";
        public Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, long> FirstSeen = new Dictionary<string, long>(StringComparer.Ordinal);

        public object ToJson() => new Dictionary<string, object>
        {
            ["naming"] = Naming, ["movie_layout"] = MovieLayout, ["series_layout"] = SeriesLayout,
            ["paths"] = Paths, ["first_seen"] = FirstSeen,
        };

        public static BuildState FromJson(object o)
        {
            var st = new BuildState();
            if (!(o is Dictionary<string, object> d)) return st;
            st.Naming = (int)Engine.Naming.ToInt(Json.Get(d, "naming"));
            st.MovieLayout = Json.S(Json.Get(d, "movie_layout")) is var ml && ml.Length > 0 ? ml : "flat";
            st.SeriesLayout = Json.S(Json.Get(d, "series_layout")) is var sl && sl.Length > 0 ? sl : "flat";
            if (Json.Get(d, "paths") is Dictionary<string, object> p)
                foreach (var kv in p) st.Paths[kv.Key] = Json.S(kv.Value);
            if (Json.Get(d, "first_seen") is Dictionary<string, object> f)
                foreach (var kv in f) st.FirstSeen[kv.Key] = Engine.Naming.ToInt(kv.Value);
            return st;
        }
    }

    public sealed class BuildOptions
    {
        public bool StripPrefix = true;
        public Dictionary<string, string> MovieOverrides = new Dictionary<string, string>();
        public Dictionary<string, string> SeriesOverrides = new Dictionary<string, string>();
        public string MovieLayout = "flat";
        public string SeriesLayout = "flat";
        public Func<string, string, string> MovieUrl;   // (stream id, ext)
        public Func<string, string, string> EpisodeUrl; // (episode id, ext)
        public long Now = Naming.Now();
    }

    public static class TreeBuilder
    {
        static readonly Regex TmdbSuffix = new Regex(@"\s*\{tmdb-\d+\}$", RegexOptions.Compiled);
        static readonly Regex YearSuffix = new Regex(@"\s*\((19|20)\d{2}\)( \(\d+\))?$", RegexOptions.Compiled);
        static readonly Regex CopySuffix = new Regex(@" \(\d+\)$", RegexOptions.Compiled);
        static readonly Regex DolbyVision = new Regex(@"dolby\s*vision", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static string Get(Dictionary<string, string> d, string k) => k != null && d.TryGetValue(k, out var v) ? v : null;

        public static (Tree tree, BuildState state) Build(Catalog catalog, BuildState state, BuildOptions o)
        {
            state = state ?? new BuildState();
            var oldPaths = state.Naming == Naming.NamingVersion
                ? new Dictionary<string, string>(state.Paths, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            if ((state.MovieLayout ?? "flat") != o.MovieLayout)
                foreach (var k in oldPaths.Keys.Where(k => k.StartsWith("m", StringComparison.Ordinal)).ToList()) oldPaths.Remove(k);
            if ((state.SeriesLayout ?? "flat") != o.SeriesLayout)
                foreach (var k in oldPaths.Keys.Where(k => k.StartsWith("s", StringComparison.Ordinal)).ToList()) oldPaths.Remove(k);
            var movieCatNames = catalog.MovieCategories;
            var seriesCatNames = catalog.SeriesCategories;
            var oldSeen = state.FirstSeen;
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var firstSeen = new Dictionary<string, long>(StringComparer.Ordinal);

            long Seen(string key, object providerTs)
            {
                long t = oldSeen.TryGetValue(key, out var old) && old != 0 ? old : 0;
                if (t == 0) t = Naming.ToTs(providerTs, 0);
                if (t == 0) t = o.Now;
                firstSeen[key] = t;
                return t;
            }

            var tree = new Tree();
            tree.Dir("/").Add("Movies");
            tree.Dir("/").Add("TV");
            tree.Dir("/Movies");
            tree.Dir("/TV");

            // ---- Movies ---------------------------------------------------------
            // The same title listed twice (e.g. two qualities) goes in one folder as
            // separate files, which media servers show as versions of one movie.
            var movies = new List<Dictionary<string, object>>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in catalog.Movies)
            {
                var sid = Json.S(Json.Get(m, "stream_id"));
                if (!seenIds.Add(sid)) continue;
                movies.Add(m);
            }
            // Titles that already have a path go first, so they keep it. Regular
            // copies before 4K ones, so the 4K copy is the one labelled "- 4K".
            movies = movies
                .OrderBy(m => oldPaths.ContainsKey("m" + Json.S(Json.Get(m, "stream_id"))) ? 0 : 1)
                .ThenBy(m => Naming.QualityRe.IsMatch(Json.S(Json.Get(m, "name"))) ? 1 : 0)
                .ToList();
            foreach (var m in movies)
            {
                var sid = Json.S(Json.Get(m, "stream_id"));
                var key = "m" + sid;
                var ext = Naming.ExtOf(Json.Get(m, "container_extension"));
                var root = Get(o.MovieOverrides, Json.S(Naming.TmdbOf(m)));
                if (string.IsNullOrEmpty(root)) root = Json.S(Json.Get(m, "_folder"));
                if (string.IsNullOrEmpty(root)) root = "Movies";
                tree.Dir("/").Add(root);
                tree.Dir("/" + root);
                var path = Get(oldPaths, key);
                if (string.IsNullOrEmpty(path) || tree.Files.ContainsKey(path)
                    || !path.StartsWith("/" + root + "/", StringComparison.Ordinal))
                {
                    var (title, year) = Naming.SplitYear(Naming.CleanTitle(Json.Get(m, "name"), o.StripPrefix),
                                                         Json.Or(Json.Get(m, "year"), Json.Get(m, "releaseDate")));
                    var bas = Naming.Safe(year != null ? $"{title} ({year})" : title);
                    var parent = "/" + root;
                    if (o.MovieLayout == "category" && root == "Movies")
                    {
                        // Movies/<category name>/<title>: each title has one provider category.
                        var cat = Get(movieCatNames, Json.S(Json.Or(Json.Get(m, "category_id"), "")));
                        if (string.IsNullOrEmpty(cat)) cat = Get(movieCatNames, Json.S(Json.Or(Json.Get(m, "_cat"), "")));
                        if (string.IsNullOrEmpty(cat)) cat = "Other";
                        parent = "/" + root + "/" + Naming.Safe(cat);
                    }
                    var folder = parent + "/" + Naming.Safe(bas + Naming.TmdbTag(Naming.TmdbOf(m)));
                    var label = Naming.QualityRe.IsMatch(Json.S(Json.Get(m, "name"))) ? "4K" : null;
                    path = $"{folder}/{bas}.{ext}";
                    if (label != null && tree.Files.ContainsKey(path)) path = $"{folder}/{bas} - {label}.{ext}";
                    if (tree.Files.ContainsKey(path)) path = $"{folder}/{bas} - {sid}.{ext}";
                }
                paths[key] = path;
                tree.AddFile(path, new Entry
                {
                    Url = o.MovieUrl(sid, ext), Key = key, Mtime = Seen(key, Json.Get(m, "added")),
                    Kind = 'm', Item = m,
                });
            }

            // ---- Series ---------------------------------------------------------
            // The same show listed more than once (a 4K and a regular copy, say)
            // goes in one folder, with the extra copies' episodes labelled
            // ("Show - S01E01 - 4K"); media servers show them as versions of one episode.
            var shows = catalog.Series.Where(s => Json.Truthy(Json.Get(Json.Get(s, "_info"), "episodes"))).ToList();

            string ShowRoot(Dictionary<string, object> s)
            {
                var r = Get(o.SeriesOverrides, Json.S(Naming.TmdbOf(s)));
                if (string.IsNullOrEmpty(r)) r = Json.S(Json.Get(s, "_folder"));
                return string.IsNullOrEmpty(r) ? "TV" : r;
            }

            string CopyLabel(Dictionary<string, object> s)
            {
                var cat = Get(seriesCatNames, Json.S(Json.Or(Json.Get(s, "category_id"), ""))) ?? "";
                if (DolbyVision.IsMatch(cat)) return "4K DV";
                if (Naming.QualityRe.IsMatch(cat) || Naming.QualityRe.IsMatch(Json.S(Json.Get(s, "name")))) return "4K";
                return null;
            }

            var groups = new List<(string root, List<Dictionary<string, object>> members)>();
            var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in shows)
            {
                var t = Json.S(Naming.TmdbOf(s)).Trim();
                var ident = Naming.IsDigits(t) && t != "0" ? t : "sid" + Json.S(Json.Get(s, "series_id"));
                var root = ShowRoot(s);
                var gk = root + "\u0001" + ident;
                if (!groupIndex.TryGetValue(gk, out var gi))
                {
                    gi = groups.Count;
                    groupIndex[gk] = gi;
                    groups.Add((root, new List<Dictionary<string, object>>()));
                }
                groups[gi].members.Add(s);
            }
            var groupList = new List<(string root, List<Dictionary<string, object>> members)>();
            foreach (var g in groups)
            {
                // The regular copy first (it gets the unlabelled files), then any
                // copy that already had a path.
                var sorted = g.members
                    .OrderBy(m => CopyLabel(m) != null ? 1 : 0)
                    .ThenBy(m => oldPaths.ContainsKey("s" + Json.S(Json.Get(m, "series_id"))) ? 0 : 1)
                    .ToList();
                groupList.Add((g.root, sorted));
            }
            // Shows that already have a path go first, so they keep it.
            groupList = groupList
                .OrderBy(g => g.members.Any(m => oldPaths.ContainsKey("s" + Json.S(Json.Get(m, "series_id")))) ? 0 : 1)
                .ToList();

            foreach (var (root, members) in groupList)
            {
                var s = members[0];
                var (title, year) = Naming.SplitYear(Naming.CleanTitle(Json.Get(s, "name"), o.StripPrefix),
                                                     Json.Or(Json.Get(s, "year"), Json.Get(s, "releaseDate")));
                tree.Dir("/").Add(root);
                tree.Dir("/" + root);
                // Reuse an earlier folder of any copy, preferring one without a "(2)" suffix.
                var olds = members.Select(m => Get(oldPaths, "s" + Json.S(Json.Get(m, "series_id"))))
                    .Where(f => !string.IsNullOrEmpty(f) && !tree.Dirs.ContainsKey(f)
                                && f.StartsWith("/" + root + "/", StringComparison.Ordinal))
                    .OrderBy(f => CopySuffix.IsMatch(f) ? 1 : 0)
                    .ToList();
                var folder = olds.Count > 0 ? olds[0] : null;
                if (folder == null)
                {
                    var show = Naming.Safe(year != null ? $"{title} ({year})" : title);
                    var parent = "/" + root;
                    if (o.SeriesLayout == "category" && root == "TV")
                    {
                        // TV/<category name>/<show>: each show is filed under one category.
                        var cat = Get(seriesCatNames, Json.S(Json.Or(Json.Get(s, "category_id"), "")));
                        if (string.IsNullOrEmpty(cat)) cat = "Other";
                        parent = "/" + root + "/" + Naming.Safe(cat);
                    }
                    folder = tree.Unique(parent + "/" + Naming.Safe(show + Naming.TmdbTag(Naming.TmdbOf(s))));
                }
                // Register the show folder and any category folder above it.
                var slash = folder.LastIndexOf('/');
                var parentDir = folder.Substring(0, slash);
                var name = folder.Substring(slash + 1);
                if (parentDir != "/" + root)
                    tree.Dir("/" + root).Add(parentDir.Substring(parentDir.LastIndexOf('/') + 1));
                tree.Dir(parentDir).Add(name);
                tree.Dir(folder);
                // Episode file names come from the show folder, so they stay stable too.
                var epTitle = TmdbSuffix.Replace(name, "");
                epTitle = YearSuffix.Replace(epTitle, "");
                if (epTitle.Length == 0) epTitle = title;
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    paths["s" + Json.S(Json.Get(m, "series_id"))] = folder;
                    var label = i > 0 ? CopyLabel(m) : null;
                    foreach (var (seasonKey, eps) in EpisodeGroups(m))
                    {
                        foreach (var epObj in eps)
                        {
                            if (!(epObj is Dictionary<string, object> ep)) continue;
                            var eid = Json.S(Json.Get(ep, "id"));
                            var key = "e" + eid;
                            var season = Naming.ToInt(Json.Or(Json.Get(ep, "season"), seasonKey));
                            var num = Naming.ToInt(Json.Get(ep, "episode_num"));
                            var ext = Naming.ExtOf(Json.Get(ep, "container_extension"));
                            var bas = $"{epTitle} - S{season:00}E{num:00}" + (label != null ? " - " + label : "");
                            var path = $"{folder}/Season {season:00}/{Naming.Safe(bas + "." + ext)}";
                            if (tree.Files.ContainsKey(path))
                                path = path.Substring(0, path.Length - ext.Length - 1) + $" - {eid}.{ext}";
                            tree.AddFile(path, new Entry
                            {
                                Url = o.EpisodeUrl(eid, ext), Key = key,
                                Mtime = Seen(key, Json.Or(Json.Get(ep, "added"), Json.Get(m, "last_modified"))),
                                Kind = 'e', Item = ep, Show = m,
                            });
                        }
                    }
                }
            }

            var newState = new BuildState
            {
                Naming = Naming.NamingVersion, MovieLayout = o.MovieLayout, SeriesLayout = o.SeriesLayout,
                Paths = paths, FirstSeen = firstSeen,
            };
            return (tree, newState);
        }

        /// <summary>A show's episodes as (season key, episode list), whether the
        /// provider sent a dict of seasons or a list of lists.</summary>
        public static IEnumerable<(string, List<object>)> EpisodeGroups(Dictionary<string, object> show)
        {
            var eps = Json.Get(Json.Get(show, "_info"), "episodes");
            if (eps is Dictionary<string, object> d)
            {
                foreach (var kv in d)
                    yield return (kv.Key, kv.Value as List<object> ?? new List<object>());
            }
            else if (eps is List<object> lists)
            {
                for (int i = 0; i < lists.Count; i++)
                    yield return ((i + 1).ToString(), lists[i] as List<object> ?? new List<object>());
            }
        }
    }
}
