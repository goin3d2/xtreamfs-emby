using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XtreamFS.Engine
{
    /// <summary>What the provider offers, for the selected categories.</summary>
    public sealed class Catalog
    {
        public List<Dictionary<string, object>> Movies = new List<Dictionary<string, object>>();
        public List<Dictionary<string, object>> Series = new List<Dictionary<string, object>>();
        public Dictionary<string, string> MovieCategories = new Dictionary<string, string>();
        public Dictionary<string, string> SeriesCategories = new Dictionary<string, string>();
        public Dictionary<string, long> MovieCounts = new Dictionary<string, long>();
        public Dictionary<string, long> SeriesCounts = new Dictionary<string, long>();
        public long Fetched;
        public string SelectionKey = "";
    }

    /// <summary>The settings that decide what's fetched and how it's laid out.</summary>
    public sealed class Selection
    {
        public List<string> MovieCats = new List<string>(), SeriesCats = new List<string>();
        public List<string> MovieExclude = new List<string>(), SeriesExclude = new List<string>();
        public Dictionary<string, string> MovieFolders = new Dictionary<string, string>();
        public Dictionary<string, string> SeriesFolders = new Dictionary<string, string>();
        public Dictionary<string, string> MovieOverrides = new Dictionary<string, string>();
        public Dictionary<string, string> SeriesOverrides = new Dictionary<string, string>();

        public static Selection From(PluginConfiguration c)
        {
            var s = new Selection
            {
                MovieCats = List(c.MovieCategories), SeriesCats = List(c.SeriesCategories),
                MovieExclude = List(c.MovieExclude), SeriesExclude = List(c.SeriesExclude),
                MovieFolders = Map(c.MovieFolders), SeriesFolders = Map(c.SeriesFolders),
                MovieOverrides = Overrides(c.MovieOverrides), SeriesOverrides = Overrides(c.SeriesOverrides),
            };
            // Mapped categories are included automatically.
            foreach (var cid in s.MovieFolders.Keys) if (!s.MovieCats.Contains(cid)) s.MovieCats.Add(cid);
            foreach (var cid in s.SeriesFolders.Keys) if (!s.SeriesCats.Contains(cid)) s.SeriesCats.Add(cid);
            return s;
        }

        /// <summary>A fingerprint of the selection, to tell a changed selection from a provider shrink.</summary>
        public string Key() => Json.Write(new Dictionary<string, object>
        {
            ["movies"] = MovieCats, ["series"] = SeriesCats, ["movie_exclude"] = MovieExclude,
            ["series_exclude"] = SeriesExclude, ["movie_folders"] = MovieFolders, ["series_folders"] = SeriesFolders,
        });

        public static List<string> List(string s) =>
            (s ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public static Dictionary<string, string> Map(string s)
        {
            var o = new Dictionary<string, string>();
            foreach (var part in (s ?? "").Split(','))
            {
                var i = part.IndexOf('=');
                if (i < 0) continue;
                var cid = part.Substring(0, i).Trim();
                var folder = part.Substring(i + 1).Trim();
                if (cid.Length > 0 && folder.Length > 0) o[cid] = Naming.Safe(folder);
            }
            return o;
        }

        public static Dictionary<string, string> Overrides(string s)
        {
            var o = new Dictionary<string, string>();
            foreach (var group in (s ?? "").Split(';'))
            {
                var i = group.LastIndexOf('=');
                if (i < 0) continue;
                var folder = group.Substring(i + 1).Trim();
                if (folder.Length == 0) continue;
                foreach (var tid in group.Substring(0, i).Split(','))
                {
                    var t = tid.Trim();
                    if (Naming.IsDigits(t)) o[t] = Naming.Safe(folder);
                }
            }
            return o;
        }

        public static bool IsAll(List<string> wanted) =>
            wanted.Any(w => w.Trim().Equals("all", StringComparison.OrdinalIgnoreCase));

        /// <summary>Match wanted entries (IDs or names, or "all") to category IDs, minus excluded ones.</summary>
        public static List<string> Resolve(List<string> wanted, IList<KeyValuePair<string, string>> available,
                                           List<string> exclude, Action<string> warn = null)
        {
            var o = new List<string>();
            if (wanted.Count == 0) return o;
            var byId = new Dictionary<string, string>();
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in available)
            {
                byId[kv.Key] = kv.Value;
                byName[(kv.Value ?? "").Trim()] = kv.Key;
            }
            if (IsAll(wanted)) o.AddRange(available.Select(kv => kv.Key));
            foreach (var w in wanted)
            {
                if (w.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) continue;
                string id = byId.ContainsKey(w) ? w : (byName.TryGetValue(w, out var n) ? n : null);
                if (id != null) { if (!o.Contains(id)) o.Add(id); }
                else warn?.Invoke($"Category '{w}' not found at provider; skipping");
            }
            var drop = new HashSet<string>();
            foreach (var x in exclude)
            {
                string id = byId.ContainsKey(x) ? x : (byName.TryGetValue(x, out var n) ? n : null);
                if (id != null) drop.Add(id);
            }
            return o.Where(cid => !drop.Contains(cid)).ToList();
        }

        /// <summary>A category's own folder, looked up by ID or by name.</summary>
        public static string FolderFor(Dictionary<string, string> mapping, string cid, string name)
        {
            if (mapping.Count == 0) return null;
            if (mapping.TryGetValue(cid, out var f)) return f;
            var lname = (name ?? "").Trim();
            foreach (var kv in mapping)
                if (kv.Key.Trim().Equals(lname, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
    }

    /// <summary>Talks to the provider's Xtream API.</summary>
    public sealed class XtreamClient
    {
        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        public string Url, User, Password, UserAgent;
        public double Delay;

        public XtreamClient(string url, string user, string password, string userAgent, double delay)
        {
            Url = (url ?? "").Trim().TrimEnd('/');
            User = (user ?? "").Trim();
            Password = password ?? "";
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? "VLC/3.0.20 LibVLC/3.0.20" : userAgent;
            Delay = delay;
        }

        public string MovieUrl(string sid, string ext) => $"{Url}/movie/{User}/{Password}/{sid}.{ext}";
        public string EpisodeUrl(string eid, string ext) => $"{Url}/series/{User}/{Password}/{eid}.{ext}";

        public static string Query(IEnumerable<KeyValuePair<string, string>> ps) =>
            string.Join("&", ps.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? "")));

        public async Task<(int status, string body)> GetRaw(string url, CancellationToken ct, int timeoutSeconds = 60)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ((int)resp.StatusCode, body);
                }
            }
        }

        public async Task<object> Api(string action, CancellationToken ct, params (string, string)[] extra)
        {
            var ps = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("username", User),
                new KeyValuePair<string, string>("password", Password),
                new KeyValuePair<string, string>("action", action),
            };
            foreach (var (k, v) in extra) ps.Add(new KeyValuePair<string, string>(k, v));
            var (status, body) = await GetRaw($"{Url}/player_api.php?{Query(ps)}", ct).ConfigureAwait(false);
            if (status < 200 || status >= 300) throw new HttpRequestException($"HTTP {status} from the provider for {action}");
            var data = string.IsNullOrWhiteSpace(body) ? null : Json.Parse(body);
            if (Delay > 0) await Task.Delay(TimeSpan.FromSeconds(Delay), ct).ConfigureAwait(false);
            return data;
        }

        public async Task<List<KeyValuePair<string, string>>> Categories(string action, CancellationToken ct)
        {
            var o = new List<KeyValuePair<string, string>>();
            if (await Api(action, ct).ConfigureAwait(false) is List<object> l)
                foreach (var c in l)
                    o.Add(new KeyValuePair<string, string>(Json.S(Json.Get(c, "category_id")),
                                                           Json.S(Json.Get(c, "category_name"))));
            return o;
        }
    }

    /// <summary>Pulls the selected categories from the provider.</summary>
    public sealed class CatalogFetcher
    {
        readonly XtreamClient _xt;
        readonly Selection _sel;
        readonly string _cacheDir;
        readonly Status _status;
        readonly LogRing _log;

        public CatalogFetcher(XtreamClient xt, Selection sel, string cacheDir, Status status, LogRing log)
        {
            _xt = xt; _sel = sel; _cacheDir = cacheDir; _status = status; _log = log;
        }

        static readonly string[] MovieKeys = { "stream_id", "name", "tmdb_id", "tmdb", "category_id", "container_extension",
            "added", "year", "releaseDate", "rating", "plot", "genre", "stream_icon" };
        static readonly string[] SeriesKeys = { "series_id", "name", "tmdb_id", "tmdb", "category_id", "cover", "plot",
            "cast", "director", "genre", "releaseDate", "year", "rating", "backdrop_path", "last_modified" };
        static readonly string[] EpisodeKeys = { "id", "episode_num", "season", "title", "container_extension", "added" };
        static readonly string[] EpisodeInfoKeys = { "movie_image", "plot", "releaseDate", "air_date", "rating", "duration_secs", "name" };

        static Dictionary<string, object> Pick(object src, string[] keys)
        {
            var o = new Dictionary<string, object>();
            if (!(src is Dictionary<string, object> d)) return o;
            foreach (var k in keys) if (d.TryGetValue(k, out var v) && v != null) o[k] = v;
            return o;
        }

        /// <summary>Keep only the episode fields that naming and .nfo files use.</summary>
        static object TrimEpisodes(object eps)
        {
            List<object> TrimList(object l)
            {
                var o = new List<object>();
                if (l is List<object> list)
                    foreach (var e in list)
                    {
                        var t = Pick(e, EpisodeKeys);
                        if (Json.Get(e, "info") is Dictionary<string, object> info) t["info"] = Pick(info, EpisodeInfoKeys);
                        o.Add(t);
                    }
                return o;
            }
            if (eps is Dictionary<string, object> d)
            {
                var o = new Dictionary<string, object>();
                foreach (var kv in d) o[kv.Key] = TrimList(kv.Value);
                return o;
            }
            if (eps is List<object> lists) return lists.Select(TrimList).Cast<object>().ToList();
            return new Dictionary<string, object>();
        }

        public async Task<Catalog> Fetch(Summary old, CancellationToken ct)
        {
            var catalog = new Catalog { Fetched = Naming.Now(), SelectionKey = _sel.Key() };

            if (_sel.MovieCats.Count > 0)
            {
                var available = await _xt.Categories("get_vod_categories", ct).ConfigureAwait(false);
                foreach (var kv in available) catalog.MovieCategories[kv.Key] = kv.Value;
                ReportChanges("movie", old?.MovieCategories, catalog.MovieCategories, Selection.IsAll(_sel.MovieCats));
                var cats = Selection.Resolve(_sel.MovieCats, available, _sel.MovieExclude, w => _log.Warn(w));
                var movies = new Dictionary<string, Dictionary<string, object>>();
                var order = new List<Dictionary<string, object>>();
                for (int n = 0; n < cats.Count; n++)
                {
                    var cid = cats[n];
                    catalog.MovieCategories.TryGetValue(cid, out var cname);
                    _status.SetPhase("movies", cname ?? cid, n, cats.Count);
                    var items = await _xt.Api("get_vod_streams", ct, ("category_id", cid)).ConfigureAwait(false) as List<object>
                                ?? new List<object>();
                    catalog.MovieCounts[cid] = items.Count;
                    _log.Info($"Movies category {cid} ({cname}): {items.Count} titles");
                    var folder = Selection.FolderFor(_sel.MovieFolders, cid, cname);
                    foreach (var raw in items)
                    {
                        var sid = Json.S(Json.Get(raw, "stream_id"));
                        if (!movies.TryGetValue(sid, out var existing))
                        {
                            var m = Pick(raw, MovieKeys);
                            m["_folder"] = folder ?? "Movies";
                            m["_cat"] = cid;
                            movies[sid] = m;
                            order.Add(m);
                        }
                        else if (folder != null) existing["_folder"] = folder; // a category with its own folder wins
                    }
                }
                catalog.Movies = order;
            }

            if (_sel.SeriesCats.Count > 0)
                catalog.Series = await FetchSeries(old, catalog, ct).ConfigureAwait(false);
            return catalog;
        }

        async Task<List<Dictionary<string, object>>> FetchSeries(Summary old, Catalog catalog, CancellationToken ct)
        {
            var cachePath = Path.Combine(_cacheDir, "episodes.json");
            var cache = Store.ReadJson(cachePath) as Dictionary<string, object> ?? new Dictionary<string, object>();

            void Save() => Store.WriteJson(cachePath, cache);

            var available = await _xt.Categories("get_series_categories", ct).ConfigureAwait(false);
            foreach (var kv in available) catalog.SeriesCategories[kv.Key] = kv.Value;
            ReportChanges("series", old?.SeriesCategories, catalog.SeriesCategories, Selection.IsAll(_sel.SeriesCats));
            var cats = Selection.Resolve(_sel.SeriesCats, available, _sel.SeriesExclude, w => _log.Warn(w));
            var shows = new List<Dictionary<string, object>>();
            var byId = new Dictionary<string, Dictionary<string, object>>();
            for (int n = 0; n < cats.Count; n++)
            {
                var cid = cats[n];
                catalog.SeriesCategories.TryGetValue(cid, out var cname);
                _status.SetPhase("series", cname ?? cid, n, cats.Count);
                var items = await _xt.Api("get_series", ct, ("category_id", cid)).ConfigureAwait(false) as List<object>
                            ?? new List<object>();
                catalog.SeriesCounts[cid] = items.Count;
                _log.Info($"Series category {cid} ({cname}): {items.Count} shows");
                var folder = Selection.FolderFor(_sel.SeriesFolders, cid, cname);
                foreach (var raw in items)
                {
                    var sid = Json.S(Json.Get(raw, "series_id"));
                    if (!byId.TryGetValue(sid, out var existing))
                    {
                        var s = Pick(raw, SeriesKeys);
                        s["_folder"] = folder ?? "TV";
                        byId[sid] = s;
                        shows.Add(s);
                    }
                    else if (folder != null) existing["_folder"] = folder;
                }
            }

            bool Fresh(Dictionary<string, object> s) =>
                cache.TryGetValue(Json.S(Json.Get(s, "series_id")), out var c)
                && Json.S(Json.Get(c, "lm")) == Json.S(Json.Get(s, "last_modified"));

            var todo = shows.Where(s => !Fresh(s)).ToList();
            var est = todo.Count * (_xt.Delay + 0.25);
            _log.Info($"Series: {shows.Count} unique shows, {shows.Count - todo.Count} cached, " +
                      $"{todo.Count} need episode lists (about {Math.Round(est / 60)} min)");
            var started = DateTime.UtcNow;
            var lastLog = started;
            _status.SetPhase("episodes", "", 0, todo.Count, (long)est);
            for (int i = 1; i <= todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = todo[i - 1];
                var sid = Json.S(Json.Get(s, "series_id"));
                try
                {
                    var info = await _xt.Api("get_series_info", ct, ("series_id", sid)).ConfigureAwait(false);
                    cache[sid] = new Dictionary<string, object>
                    {
                        ["lm"] = Json.S(Json.Get(s, "last_modified")),
                        ["episodes"] = TrimEpisodes(Json.Get(info, "episodes")),
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) // keep going on one bad series
                {
                    _log.Warn($"Series {sid} ({Json.S(Json.Get(s, "name"))}): {e.Message}");
                }
                if (i % 100 == 0) Save();
                if (i % 10 == 0 || i == todo.Count)
                {
                    var spent = (DateTime.UtcNow - started).TotalSeconds;
                    _status.SetPhase("episodes", Json.S(Json.Get(s, "name")), i, todo.Count,
                                     (long)(spent / i * (todo.Count - i)));
                }
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 30 || i == todo.Count)
                {
                    var spent = (DateTime.UtcNow - started).TotalSeconds;
                    var left = spent / i * (todo.Count - i);
                    _log.Info($"Episode lists: {i}/{todo.Count} ({i * 100 / todo.Count}%), about {Math.Round(left / 60)} min left");
                    lastLog = DateTime.UtcNow;
                }
            }
            if (todo.Count > 0) Save();

            foreach (var s in shows)
            {
                cache.TryGetValue(Json.S(Json.Get(s, "series_id")), out var c);
                s["_info"] = new Dictionary<string, object>
                {
                    ["episodes"] = Json.Get(c, "episodes") ?? new Dictionary<string, object>()
                };
            }
            return shows;
        }

        void ReportChanges(string kind, Dictionary<string, string> old, Dictionary<string, string> now, bool selectedAll)
        {
            if (old == null || old.Count == 0) return;
            var added = now.Where(kv => !old.ContainsKey(kv.Key)).Select(kv => $"{kv.Key} {kv.Value}").ToList();
            var removed = old.Where(kv => !now.ContainsKey(kv.Key)).Select(kv => $"{kv.Key} {kv.Value}").ToList();
            if (added.Count == 0 && removed.Count == 0) return;
            _status.AddChange(kind, added, removed);
            if (added.Count > 0)
                _log.Info($"Provider added {kind} categories: {string.Join(", ", added)}" +
                          (selectedAll ? "" : $" (not included; tick them on the Categories tab to add them)"));
            if (removed.Count > 0)
                _log.Warn($"Provider removed {kind} categories: {string.Join(", ", removed)}");
        }
    }
}
