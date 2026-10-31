using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XtreamFS.Engine
{
    /// <summary>Runs catalog refreshes and answers the dashboard page.</summary>
    public sealed class XtreamEngine
    {
        public readonly Status Status = new Status();
        public readonly LogRing Log;
        public readonly string DataDir;
        readonly Func<PluginConfiguration> _config;
        readonly SemaphoreSlim _running = new SemaphoreSlim(1, 1);
        readonly HashSet<string> _secrets = new HashSet<string>();
        Summary _summary;
        bool _summaryLoaded;
        Dictionary<string, object> _account;
        public volatile bool AcceptShrinkOnce;
        public Action ScanLibrary; // set by the plugin: queue an Emby library scan

        public XtreamEngine(string dataDir, Func<PluginConfiguration> config, Action<string, string> forwardLog)
        {
            DataDir = dataDir;
            _config = config;
            Log = new LogRing(Scrub, forwardLog);
            Directory.CreateDirectory(dataDir);
            Status.Restore(Store.ReadJson(Path.Combine(dataDir, "status.json")));
        }

        void SaveStatus()
        {
            try { Store.WriteJson(P("status.json"), Status.Persisted()); } catch { }
        }

        public bool IsRunning => _running.CurrentCount == 0;

        // ---- masking ------------------------------------------------------------

        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var c = _config();
            var secrets = new List<string>();
            lock (_secrets) secrets.AddRange(_secrets);
            if (c != null) { secrets.Add(c.Username); secrets.Add(c.Password); }
            foreach (var s in secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 3).Distinct().OrderByDescending(s => s.Length))
                text = text.Replace(s, "***");
            return text;
        }

        void Remember(params string[] secrets)
        {
            lock (_secrets) foreach (var s in secrets) if (!string.IsNullOrEmpty(s) && s.Length >= 3) _secrets.Add(s);
        }

        // ---- stored results -------------------------------------------------------

        string P(string name) => Path.Combine(DataDir, name);

        public Summary Summary
        {
            get
            {
                if (!_summaryLoaded) { _summary = Summary.FromJson(Store.ReadJson(P("summary.json"))); _summaryLoaded = true; }
                return _summary;
            }
        }

        // ---- refresh ----------------------------------------------------------------

        static bool HasLogin(PluginConfiguration c) =>
            !string.IsNullOrWhiteSpace(c.ServerUrl) && !string.IsNullOrWhiteSpace(c.Username) && !string.IsNullOrEmpty(c.Password);

        public async Task RefreshAsync(IProgress<double> progress, CancellationToken ct)
        {
            if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
            {
                Log.Info("A refresh is already running");
                return;
            }
            var c = _config();
            var sel = Selection.From(c);
            Action<string, long, long> onPhase = (phase, done, total) =>
            {
                double frac = total > 0 ? (double)done / total : 0;
                double pct;
                switch (phase)
                {
                    case "movies": pct = 2 + 10 * frac; break;
                    case "series": pct = 12 + 6 * frac; break;
                    case "episodes": pct = 18 + 67 * frac; break;
                    case "building": pct = 85; break;
                    case "writing": pct = 88 + 12 * frac; break;
                    default: pct = 1; break;
                }
                progress?.Report(pct);
            };
            Status.PhaseChanged = onPhase;
            try
            {
                Status.Started = Naming.Now();
                string missing = !HasLogin(c) ? "provider login"
                    : (sel.MovieCats.Count == 0 && sel.SeriesCats.Count == 0) ? "categories"
                    : string.IsNullOrWhiteSpace(c.StrmFolder) ? ".strm folder" : null;
                if (missing != null)
                {
                    Log.Warn($"Not refreshing: no {missing} yet. Add it on the xtreamfs page.");
                    Status.Last = new Dictionary<string, object>
                    {
                        ["time"] = Naming.Now(), ["result"] = "waiting", ["detail"] = $"Add your {missing} on the xtreamfs page.",
                    };
                    return;
                }
                Log.Info("Refreshing catalog from provider");
                Status.SetPhase("categories");
                var xt = new XtreamClient(c.ServerUrl, c.Username, c.Password, c.UserAgent, c.ApiDelay);
                var old = Summary;
                var catalog = await new CatalogFetcher(xt, sel, DataDir, Status, Log).Fetch(old, ct).ConfigureAwait(false);

                var problem = ShrinkProblem(c, old, catalog);
                if (problem != null)
                {
                    if (AcceptShrinkOnce) Log.Warn("Accepting the smaller catalog as requested: " + problem);
                    else
                    {
                        Log.Error($"Keeping the previous catalog: {problem}. If the provider really removed these, " +
                                  "accept it on the xtreamfs page.");
                        Status.Shrink = problem;
                        Status.Last = new Dictionary<string, object>
                        {
                            ["time"] = Naming.Now(), ["result"] = "kept previous", ["detail"] = problem,
                        };
                        return;
                    }
                }
                AcceptShrinkOnce = false;
                Status.Shrink = null;

                Status.SetPhase("building");
                var state = BuildState.FromJson(Store.ReadJson(P("state.json")));
                var opts = new BuildOptions
                {
                    StripPrefix = c.StripPrefix, MovieOverrides = sel.MovieOverrides, SeriesOverrides = sel.SeriesOverrides,
                    MovieLayout = c.MovieCategoryFolders ? "category" : "flat",
                    SeriesLayout = c.SeriesCategoryFolders ? "category" : "flat",
                    MovieUrl = xt.MovieUrl, EpisodeUrl = xt.EpisodeUrl,
                };
                var (tree, newState) = TreeBuilder.Build(catalog, state, opts);
                Store.WriteJson(P("state.json"), newState.ToJson());
                var summary = Summary.From(catalog, tree);
                Store.WriteJson(P("summary.json"), summary.ToJson());
                _summary = summary; _summaryLoaded = true;
                Log.Info($"Catalog ready: {tree.Files.Count} files");

                Status.SetPhase("writing", c.StrmFolder);
                var writer = new StrmWriter(c.StrmFolder, DataDir, Log, Status)
                {
                    Nfo = c.WriteNfo, LockNfo = c.LockNfo, StripPrefix = c.StripPrefix,
                };
                writer.Sync(tree, frac => Status.SetPhase("writing", c.StrmFolder, (long)(frac * 1000), 1000));
                Status.Last = new Dictionary<string, object>
                {
                    ["time"] = Naming.Now(), ["result"] = "ok", ["files"] = (long)tree.Files.Count,
                    ["movies"] = (long)catalog.Movies.Count, ["shows"] = (long)catalog.Series.Count,
                    ["seconds"] = Naming.Now() - (Status.Started ?? Naming.Now()),
                };
                progress?.Report(100);
                if (c.ScanLibraryAfterRefresh) ScanLibrary?.Invoke();
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Refresh cancelled");
                Status.Last = new Dictionary<string, object> { ["time"] = Naming.Now(), ["result"] = "cancelled" };
                throw; // so Emby's Scheduled Tasks shows it as cancelled
            }
            catch (Exception e)
            {
                Log.Error("Catalog refresh failed: " + Scrub(e.Message));
                Status.Last = new Dictionary<string, object>
                {
                    ["time"] = Naming.Now(), ["result"] = "failed", ["detail"] = Scrub(e.Message),
                };
                throw new Exception(Scrub(e.Message)); // so Scheduled Tasks shows it as failed, without the login
            }
            finally
            {
                Status.SetPhase("idle");
                Status.PhaseChanged = null;
                SaveStatus();
                _running.Release();
            }
        }

        static string ShrinkProblem(PluginConfiguration c, Summary old, Catalog now)
        {
            var g = c.ShrinkGuardPercent;
            if (g <= 0 || old == null || old.SelectionKey != now.SelectionKey) return null;
            var issues = new List<string>();
            foreach (var (kind, before, after) in new[] { ("movies", old.Movies, (long)now.Movies.Count), ("shows", old.Shows, (long)now.Series.Count) })
                if (before >= c.ShrinkGuardMin && after < before * (1 - g / 100.0))
                    issues.Add($"{kind} would drop from {before} to {after} ({100 - after * 100 / before}% fewer)");
            return issues.Count > 0 ? string.Join("; ", issues) : null;
        }

        // ---- provider login -----------------------------------------------------------

        public async Task<Dictionary<string, object>> CheckLogin(string url, string user, string password, string userAgent)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            user = (user ?? "").Trim();
            Dictionary<string, object> Fail(string m) => new Dictionary<string, object> { ["ok"] = false, ["error"] = m };
            if (url.Length == 0 || user.Length == 0 || string.IsNullOrEmpty(password))
                return Fail("Enter the server address, username and password.");
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Fail("The server address should start with http:// or https://");
            Remember(user, password);
            var xt = new XtreamClient(url, user, password, userAgent, 0);
            int status; string body;
            try
            {
                var q = XtreamClient.Query(new[]
                {
                    new KeyValuePair<string, string>("username", user), new KeyValuePair<string, string>("password", password),
                });
                (status, body) = await xt.GetRaw($"{url}/player_api.php?{q}", CancellationToken.None, 15).ConfigureAwait(false);
            }
            catch (Exception e) { return Fail("Couldn't reach the server: " + Scrub(e.InnerException?.Message ?? e.Message)); }
            if (status != 200) return Fail($"The server answered HTTP {status}.");
            object data;
            try { data = Json.Parse(body); }
            catch { return Fail("The server answered, but not like an Xtream panel. Check the address, including the port."); }
            var info = Json.Get(data, "user_info");
            if (Json.S(Json.Get(info, "auth")) != "1") return Fail("The provider rejected this username and password.");
            long? Num(object v) => long.TryParse(Json.S(v), out var n) ? n : (long?)null;
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["status"] = Json.S(Json.Get(info, "status")), ["expires"] = Num(Json.Get(info, "exp_date")),
                ["trial"] = Json.S(Json.Get(info, "is_trial")) == "1", ["created"] = Num(Json.Get(info, "created_at")),
                ["max_connections"] = Num(Json.Get(info, "max_connections")),
                ["active_connections"] = Num(Json.Get(info, "active_cons")),
                ["server_timezone"] = Json.S(Json.Get(Json.Get(data, "server_info"), "timezone")),
                ["checked"] = Naming.Now(),
            };
        }

        public async Task<Dictionary<string, object>> Account(bool fresh)
        {
            var cached = _account;
            if (cached != null && !fresh && Naming.Now() - Naming.ToInt(cached["checked"]) < 600) return cached;
            var c = _config();
            Dictionary<string, object> res;
            if (!HasLogin(c))
                res = new Dictionary<string, object> { ["ok"] = false, ["error"] = "No provider login yet.", ["checked"] = Naming.Now() };
            else
            {
                res = await CheckLogin(c.ServerUrl, c.Username, c.Password, c.UserAgent).ConfigureAwait(false);
                if (!res.ContainsKey("checked")) res["checked"] = Naming.Now();
            }
            _account = res;
            return res;
        }

        public void ForgetAccount() => _account = null;

        // ---- categories for the page ------------------------------------------------------

        public async Task<Dictionary<string, object>> Categories(bool live)
        {
            var c = _config();
            var sel = Selection.From(c);
            var known = Summary;
            var names = new Dictionary<string, Dictionary<string, string>>
            {
                ["movie"] = known?.MovieCategories ?? new Dictionary<string, string>(),
                ["series"] = known?.SeriesCategories ?? new Dictionary<string, string>(),
            };
            if (live)
            {
                if (!HasLogin(c)) throw new InvalidOperationException("Add your provider login on the Account tab first.");
                var xt = new XtreamClient(c.ServerUrl, c.Username, c.Password, c.UserAgent, 0);
                names["movie"] = (await xt.Categories("get_vod_categories", CancellationToken.None).ConfigureAwait(false))
                    .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
                names["series"] = (await xt.Categories("get_series_categories", CancellationToken.None).ConfigureAwait(false))
                    .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
            }
            var o = new Dictionary<string, object>();
            foreach (var kind in new[] { "movie", "series" })
            {
                var wanted = kind == "movie" ? sel.MovieCats : sel.SeriesCats;
                var exclude = kind == "movie" ? sel.MovieExclude : sel.SeriesExclude;
                var folders = kind == "movie" ? sel.MovieFolders : sel.SeriesFolders;
                var counts = (kind == "movie" ? known?.MovieCounts : known?.SeriesCounts) ?? new Dictionary<string, long>();
                var knownNames = kind == "movie" ? known?.MovieCategories : known?.SeriesCategories;
                var avail = names[kind].ToList();
                var included = new HashSet<string>(Selection.Resolve(wanted, avail, exclude));
                var rows = avail.Select(kv => (object)new Dictionary<string, object>
                {
                    ["id"] = kv.Key, ["name"] = kv.Value,
                    ["count"] = counts.TryGetValue(kv.Key, out var n) ? (object)n : null,
                    ["included"] = included.Contains(kv.Key),
                    ["folder"] = Selection.FolderFor(folders, kv.Key, kv.Value) ?? "",
                    ["new"] = live && knownNames != null && knownNames.Count > 0 && !knownNames.ContainsKey(kv.Key),
                }).ToList();
                o[kind] = new Dictionary<string, object> { ["all"] = Selection.IsAll(wanted), ["rows"] = rows };
            }
            return o;
        }
    }
}
