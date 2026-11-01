using System;
using System.Collections.Generic;
using System.IO;

namespace XtreamFS.Engine
{
    /// <summary>
    /// categories.json: which provider categories each written title belongs to, by the path Emby sees.
    /// Read by other plugins (e.g. Emby Collections) to build collections from provider categories.
    ///
    /// Shape (version 1):
    /// {
    ///   "version": 1, "updated": unix-seconds, "root": "/abs/strm/folder",
    ///   "movies": { "&lt;category id&gt;": { "name": "...", "items": [ { "path": "/abs/.../Title (2020).strm", "tmdb": "123" } ] } },
    ///   "series": { "&lt;category id&gt;": { "name": "...", "items": [ { "path": "/abs/.../Show (2019) {tmdb-456}", "tmdb": "456" } ] } }
    /// }
    /// Movie paths are the .strm file; series paths are the show folder.
    /// </summary>
    public static class CategoryIndex
    {
        public const int Version = 1;

        public static Dictionary<string, object> Build(Catalog catalog, BuildState state, string strmRoot)
        {
            var root = (strmRoot ?? "").TrimEnd('/', '\\');
            string Abs(string rel) => Path.Combine(root, rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            var movies = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var m in catalog.Movies)
            {
                if (!state.Paths.TryGetValue("m" + Json.S(Json.Get(m, "stream_id")), out var rel) || string.IsNullOrEmpty(rel)) continue;
                var dot = rel.LastIndexOf('.');
                if (dot > rel.LastIndexOf('/')) rel = rel.Substring(0, dot);
                Add(movies, catalog.MovieCategories, m, Abs(rel + ".strm"));
            }

            var series = new Dictionary<string, object>(StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in catalog.Series)
            {
                if (!state.Paths.TryGetValue("s" + Json.S(Json.Get(s, "series_id")), out var rel) || string.IsNullOrEmpty(rel)) continue;
                Add(series, catalog.SeriesCategories, s, Abs(rel), done);
            }

            return new Dictionary<string, object>
            {
                ["version"] = Version,
                ["updated"] = Naming.Now(),
                ["root"] = root,
                ["movies"] = movies,
                ["series"] = series,
            };
        }

        static void Add(Dictionary<string, object> into, Dictionary<string, string> names, Dictionary<string, object> item,
                        string path, HashSet<string> done = null)
        {
            var cats = Json.Get(item, "_cats") as List<object>;
            if (cats == null || cats.Count == 0) cats = new List<object> { Json.S(Json.Or(Json.Get(item, "_cat"), Json.Get(item, "category_id"))) };
            var tmdb = Json.S(Naming.TmdbOf(item)).Trim();
            foreach (var c in cats)
            {
                var cid = Json.S(c);
                if (cid.Length == 0) continue;
                // Merged copies of a show share a folder; list it once per category.
                if (done != null && !done.Add(cid + "\u0001" + path)) continue;
                if (!(into.TryGetValue(cid, out var o) && o is Dictionary<string, object> entry))
                {
                    names.TryGetValue(cid, out var name);
                    into[cid] = entry = new Dictionary<string, object> { ["name"] = name ?? cid, ["items"] = new List<object>() };
                }
                var row = new Dictionary<string, object> { ["path"] = path };
                if (Naming.IsDigits(tmdb) && tmdb != "0") row["tmdb"] = tmdb;
                ((List<object>)entry["items"]).Add(row);
            }
        }
    }
}
