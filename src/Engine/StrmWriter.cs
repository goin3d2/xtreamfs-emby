using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XtreamFS.Engine
{
    /// <summary>.nfo metadata files built from the provider's catalog.</summary>
    public static class Nfo
    {
        static readonly Regex XmlBad = new Regex(@"[\x00-\x08\x0b\x0c\x0e-\x1f]", RegexOptions.Compiled);
        static readonly Regex FirstUrl = new Regex("https?://[^\\s'\"\\],]+", RegexOptions.Compiled);
        static readonly Regex DateRe = new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
        static readonly Regex GenreSplit = new Regex(@"\s*[,/]\s*", RegexOptions.Compiled);
        static readonly Regex NoTitle = new Regex(@"S\d+\s*E\d+(?:\s*-\s*(.*\S))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string X(object v) =>
            XmlBad.Replace(v is string s ? s : Py(v), "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        /// <summary>Python str() for the values that end up in .nfo files.</summary>
        static string Py(object v)
        {
            if (v is double d) return d == Math.Floor(d) && Math.Abs(d) < 1e16
                ? d.ToString("0", CultureInfo.InvariantCulture) + ".0"
                : d.ToString("R", CultureInfo.InvariantCulture);
            if (v is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (v is int i) return i.ToString(CultureInfo.InvariantCulture);
            return Json.S(v);
        }

        static string FirstUrlOf(object v)
        {
            if (v is List<object> l) v = l.Count > 0 ? l[0] : "";
            var m = FirstUrl.Match(Json.S(v));
            return m.Success ? m.Value : "";
        }

        static double? Num(object v)
        {
            if (v == null || v is bool) return null;
            if (double.TryParse(Json.S(v).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                return d;
            return null;
        }

        static string Date(object v)
        {
            var s = Json.S(v);
            if (s.Length > 10) s = s.Substring(0, 10);
            return DateRe.IsMatch(s) ? s : "";
        }

        static string Build(string root, List<(string tag, object val)> fields)
        {
            var lines = new List<string> { "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", $"<{root}>" };
            foreach (var (tag, val) in fields)
            {
                if (val == null) continue;
                if (val is string s && s.Length == 0) continue;
                if (val is long l && l == 0) continue;
                if (val is int i && i == 0) continue;
                if (val is double d && d == 0) continue;
                if (!(val is string) && !(val is double) && !(val is long) && !(val is int) && !Json.Truthy(val)) continue;
                lines.Add(tag == "_raw" ? "  " + Json.S(val) : $"  <{tag}>{X(val)}</{tag}>");
            }
            lines.Add($"</{root}>");
            return string.Join("\n", lines) + "\n";
        }

        static IEnumerable<(string, object)> Ids(object tmdb)
        {
            if (!Json.Truthy(tmdb)) yield break;
            yield return ("_raw", $"<uniqueid type=\"tmdb\" default=\"true\">{X(tmdb)}</uniqueid>");
            yield return ("tmdbid", tmdb);
        }

        static readonly Regex TrailerId = new Regex(@"^[\w-]{6,20}\z", RegexOptions.Compiled);
        static readonly Regex ImgExt = new Regex(@"\.(jpe?g|png|webp)(?:[?#]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static List<string> Split(object v, int limit = 0)
        {
            var o = GenreSplit.Split(Json.S(Json.Truthy(v) ? v : "")).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return limit > 0 ? o.Take(limit).ToList() : o;
        }

        static string Trailer(object v)
        {
            var s = Json.S(v).Trim();
            if (TrailerId.IsMatch(s)) return "https://www.youtube.com/watch?v=" + s; // a bare YouTube video ID
            return s.StartsWith("http", StringComparison.Ordinal) ? s : "";
        }

        /// <summary>File extension for a downloaded image.</summary>
        public static string ImageExt(string url)
        {
            var m = ImgExt.Match(url ?? "");
            if (!m.Success) return "jpg";
            var e = m.Groups[1].Value.ToLowerInvariant();
            return e == "jpeg" ? "jpg" : e;
        }

        /// <summary>(poster URL, backdrop URL) for a movie, preferring its full details.</summary>
        public static (string poster, string fanart) MovieArt(Dictionary<string, object> m, Dictionary<string, object> info)
        {
            var p = FirstUrlOf(Json.Get(info, "movie_image"));
            if (p.Length == 0) p = FirstUrlOf(Json.Get(m, "stream_icon"));
            return (p, FirstUrlOf(Json.Get(info, "backdrop")));
        }

        /// <summary>A movie's .nfo. <paramref name="info"/> is its trimmed get_vod_info details, when fetched.</summary>
        public static string Movie(Dictionary<string, object> m, bool stripPrefix, Dictionary<string, object> info = null)
        {
            info = info ?? new Dictionary<string, object>();
            var released = Date(Json.Or(Json.Get(info, "releasedate"), Json.Get(m, "releaseDate")));
            object yearField = Json.Or(Json.Or(Json.Get(m, "year"), Json.Get(m, "releaseDate")),
                                       released.Length >= 4 ? released.Substring(0, 4) : null);
            var (title, year) = Naming.SplitYear(Naming.CleanTitle(Json.Get(m, "name"), stripPrefix), yearField);
            var secs = Num(Json.Get(info, "duration_secs"));
            var (poster, fanart) = MovieArt(m, info);
            var f = new List<(string, object)>
            {
                ("title", title), ("year", year),
                ("rating", Num(Json.Get(info, "rating")) ?? Num(Json.Get(m, "rating"))),
                ("plot", Json.Or(Json.Get(info, "plot"), Json.Get(m, "plot"))),
                ("runtime", secs.HasValue && secs.Value >= 60 ? (object)(long)Math.Floor(secs.Value / 60) : null),
                ("premiered", released), ("mpaa", Json.Get(info, "mpaa")),
            };
            foreach (var g in Split(Json.Or(Json.Get(info, "genre"), Json.Get(m, "genre")))) f.Add(("genre", g));
            foreach (var d in Split(Json.Get(info, "director"))) f.Add(("director", d));
            f.AddRange(Ids(Naming.TmdbOf(m)));
            if (poster.Length > 0) f.Add(("_raw", $"<thumb aspect=\"poster\">{X(poster)}</thumb>"));
            if (fanart.Length > 0) f.Add(("_raw", $"<fanart><thumb>{X(fanart)}</thumb></fanart>"));
            foreach (var name in Split(Json.Get(info, "cast"), 20)) f.Add(("_raw", $"<actor><name>{X(name)}</name></actor>"));
            f.Add(("trailer", Trailer(Json.Get(info, "youtube_trailer"))));
            return Build("movie", f);
        }

        public static string Show(Dictionary<string, object> s, bool stripPrefix)
        {
            var (title, year) = Naming.SplitYear(Naming.CleanTitle(Json.Get(s, "name"), stripPrefix),
                                                 Json.Or(Json.Get(s, "year"), Json.Get(s, "releaseDate")));
            var f = new List<(string, object)>
            {
                ("title", title), ("year", year), ("plot", Json.Get(s, "plot")),
                ("premiered", Date(Json.Get(s, "releaseDate"))), ("rating", Num(Json.Get(s, "rating"))),
            };
            foreach (var g in GenreSplit.Split(Json.S(Json.Get(s, "genre"))))
                if (g.Length > 0) f.Add(("genre", g));
            f.AddRange(Ids(Naming.TmdbOf(s)));
            var poster = FirstUrlOf(Json.Get(s, "cover"));
            var fanart = FirstUrlOf(Json.Get(s, "backdrop_path"));
            if (poster.Length > 0) f.Add(("_raw", $"<thumb aspect=\"poster\">{X(poster)}</thumb>"));
            if (fanart.Length > 0) f.Add(("_raw", $"<fanart><thumb>{X(fanart)}</thumb></fanart>"));
            foreach (var name in Json.S(Json.Get(s, "cast")).Split(',').Take(20))
                if (name.Trim().Length > 0) f.Add(("_raw", $"<actor><name>{X(name.Trim())}</name></actor>"));
            if (Json.Truthy(Json.Get(s, "director"))) f.Add(("director", Json.Get(s, "director")));
            return Build("tvshow", f);
        }

        public static string Episode(Dictionary<string, object> ep, long? season, long? num)
        {
            var info = Json.Get(ep, "info") as Dictionary<string, object> ?? new Dictionary<string, object>();
            var secs = Num(Json.Get(info, "duration_secs"));
            var thumb = FirstUrlOf(Json.Get(info, "movie_image"));
            var title = Json.S(Json.Get(ep, "title"));
            // Providers often title episodes "Show S01E02" (no real title) or
            // "Show - S01E02 - Real Title"; keep only the real title.
            var m = NoTitle.Match(title);
            if (m.Success) title = m.Groups[1].Success ? m.Groups[1].Value : Json.S(Json.Get(info, "name"));
            return Build("episodedetails", new List<(string, object)>
            {
                ("title", title), ("season", season), ("episode", num),
                ("plot", Json.Get(info, "plot")),
                ("aired", Date(Json.Or(Json.Get(info, "releaseDate"), Json.Get(info, "air_date")))),
                ("rating", Num(Json.Get(info, "rating"))),
                ("runtime", secs.HasValue ? (object)(long)Math.Floor(secs.Value / 60) : null),
                ("_raw", thumb.Length > 0 ? $"<thumb>{X(thumb)}</thumb>" : ""),
            });
        }
    }

    /// <summary>
    /// Keeps a folder of .strm files (and optional .nfo files) in step with the tree.
    /// Only new or changed files are written and removed titles are deleted.
    /// </summary>
    public sealed class StrmWriter
    {
        static readonly Regex SxE = new Regex(@"S(\d+)E(\d+)", RegexOptions.Compiled);
        readonly string _root, _manifestPath;
        readonly LogRing _log;
        readonly Status _status;
        public bool Nfo, LockNfo, StripPrefix = true;
        public bool MovieDetailsOn, Artwork;      // full movie details in .nfo files; poster/fanart downloads
        public Dictionary<string, object> Details; // movie_details.json cache
        public string UserAgent;
        Dictionary<string, string> _want;
        List<(string rel, string url, long mtime)> _downloads = new List<(string, string, long)>();
        static readonly System.Net.Http.HttpClient ImageHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>Images the last sync found missing or changed; DownloadArtAsync fetches them.</summary>
        public int PendingArt => _downloads.Count;

        public StrmWriter(string root, string dataDir, LogRing log, Status status)
        {
            _root = root.TrimEnd('/', '\\');
            _manifestPath = Path.Combine(dataDir, "strm_manifest.json");
            _log = log; _status = status;
        }

        static string Sha16(string text)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return string.Concat(b.Take(8).Select(x => x.ToString("x2")));
            }
        }

        string Full(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

        public void Sync(Tree tree, Action<double> progress = null)
        {
            var t0 = DateTime.UtcNow;
            _status.Strm = new Dictionary<string, object> { ["running"] = true, ["started"] = Naming.Now() };
            try
            {
                var old = (Store.ReadJson(_manifestPath) as Dictionary<string, object>)
                          ?.ToDictionary(kv => kv.Key, kv => Json.S(kv.Value)) ?? new Dictionary<string, string>();
                var want = new Dictionary<string, string>(StringComparer.Ordinal);
                long written = 0, removed = 0, nfos = 0;

                void Put(string rel, string text, string tag, long mtime)
                {
                    want[rel] = tag;
                    var full = Full(rel);
                    if (old.TryGetValue(rel, out var had) && had == tag && File.Exists(full)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    var tmp = full + ".tmp";
                    File.WriteAllText(tmp, text, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(tmp, DateTimeOffset.FromUnixTimeSeconds(mtime).UtcDateTime);
                    if (File.Exists(full)) File.Delete(full);
                    File.Move(tmp, full);
                    written++;
                }

                void PutNfo(string rel, string text, long mtime)
                {
                    nfos++;
                    if (LockNfo)
                    {
                        var end = text.LastIndexOf("</", StringComparison.Ordinal);
                        text = text.Substring(0, end) + "  <lockdata>true</lockdata>\n" + text.Substring(end);
                    }
                    Put(rel, text, "nfo:" + Sha16(text), mtime);
                }

                var showsDone = new HashSet<string>(StringComparer.Ordinal);
                // movie folder -> (file name, URL, mtime); folders in first-seen order
                var art = new Dictionary<string, List<(string name, string url, long mtime)>>(StringComparer.Ordinal);
                int n = 0, total = tree.Files.Count;
                foreach (var kv in tree.Files)
                {
                    var path = kv.Key; var e = kv.Value;
                    var rel0 = path.TrimStart('/');
                    var dot = rel0.LastIndexOf('.');
                    var slash = rel0.LastIndexOf('/');
                    var bas = dot > slash ? rel0.Substring(0, dot) : rel0;
                    Put(bas + ".strm", e.Url + "\n", e.Url, e.Mtime);
                    var movie = e.Kind == 'm';
                    if (Nfo || (movie && (MovieDetailsOn || Artwork)))
                    {
                        try
                        {
                            if (movie)
                            {
                                var info = MovieDetailsOn ? MovieDetails.Of(Details, e.Item) : null;
                                if (Nfo || MovieDetailsOn) PutNfo(bas + ".nfo", Engine.Nfo.Movie(e.Item, StripPrefix, info), e.Mtime);
                                var cut = bas.LastIndexOf('/');
                                var folder = cut > 0 ? bas.Substring(0, cut) : "";
                                if (Artwork && folder.Length > 0 && !art.ContainsKey(folder))
                                {
                                    var list = new List<(string, string, long)>();
                                    var (poster, fanart) = Engine.Nfo.MovieArt(e.Item, info);
                                    if (poster.Length > 0) list.Add(("poster." + Engine.Nfo.ImageExt(poster), poster, e.Mtime));
                                    if (fanart.Length > 0) list.Add(("fanart." + Engine.Nfo.ImageExt(fanart), fanart, e.Mtime));
                                    art[folder] = list;
                                }
                            }
                            else
                            {
                                var m = SxE.Match(bas.Substring(bas.LastIndexOf('/') + 1));
                                long? season = m.Success ? long.Parse(m.Groups[1].Value) : (long?)null;
                                long? num = m.Success ? long.Parse(m.Groups[2].Value) : (long?)null;
                                PutNfo(bas + ".nfo", Engine.Nfo.Episode(e.Item, season, num), e.Mtime);
                                var seasonDir = bas.Substring(0, bas.LastIndexOf('/'));
                                var showDir = seasonDir.Substring(0, Math.Max(0, seasonDir.LastIndexOf('/')));
                                if (showsDone.Add(showDir))
                                    PutNfo(showDir + "/tvshow.nfo", Engine.Nfo.Show(e.Show, StripPrefix), e.Mtime);
                            }
                        }
                        catch (Exception ex) { _log.Warn($"NFO for {path} skipped: {ex.Message}"); }
                    }
                    if (++n % 2000 == 0) progress?.Invoke((double)n / Math.Max(1, total));
                }
                // Artwork already on disk is kept; new or changed images are downloaded afterwards.
                // An image waiting to be replaced keeps its old file (and entry) until then.
                _downloads = new List<(string, string, long)>();
                foreach (var kv in art)
                    foreach (var (name, url, mtime) in kv.Value)
                    {
                        var rel = kv.Key + "/" + name;
                        var tag = "img:" + url;
                        if (old.TryGetValue(rel, out var had) && had == tag && File.Exists(Full(rel))) want[rel] = tag;
                        else
                        {
                            if (had != null) want[rel] = had;
                            _downloads.Add((rel, url, mtime));
                        }
                    }
                foreach (var rel in old.Keys)
                {
                    if (want.ContainsKey(rel)) continue;
                    var full = Full(rel);
                    try { if (File.Exists(full)) { File.Delete(full); removed++; } } catch { }
                    // Remove folders left empty, up to the root.
                    var d = Path.GetDirectoryName(full);
                    while (d != null && d.Length > _root.Length && d.StartsWith(_root, StringComparison.Ordinal))
                    {
                        try
                        {
                            if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                            else break;
                        }
                        catch { break; }
                        d = Path.GetDirectoryName(d);
                    }
                }
                Store.WriteJson(_manifestPath, want);
                _want = want;
                var secs = (long)(DateTime.UtcNow - t0).TotalSeconds;
                _log.Info($"STRM: {want.Count} files in {_root}, {nfos} of them .nfo ({written} written, {removed} removed) in {secs}s");
                _status.Strm = new Dictionary<string, object>
                {
                    ["running"] = false, ["time"] = Naming.Now(), ["files"] = (long)want.Count, ["nfo"] = nfos,
                    ["written"] = written, ["removed"] = removed, ["seconds"] = secs,
                };
                if (_downloads.Count > 0) _status.Strm["art"] = new List<object> { 0L, (long)_downloads.Count };
            }
            catch (Exception ex)
            {
                _log.Error("STRM sync failed: " + ex.Message);
                _status.Strm = new Dictionary<string, object> { ["running"] = false, ["time"] = Naming.Now(), ["error"] = ex.Message };
                throw;
            }
        }

        /// <summary>Download the posters and backdrops the last sync found missing. Returns how many were saved.</summary>
        public async System.Threading.Tasks.Task<long> DownloadArtAsync(System.Threading.CancellationToken ct, Action<long, long> progress = null, int workers = 4)
        {
            var jobs = _downloads;
            var want = _want;
            if (jobs.Count == 0 || want == null) return 0;
            _log.Info($"Artwork: downloading {jobs.Count} images");
            var t0 = DateTime.UtcNow;
            var queue = new System.Collections.Concurrent.ConcurrentQueue<(string rel, string url, long mtime)>(jobs);
            long saved = 0, failed = 0, done = 0;
            var gate = new object();
            var ua = string.IsNullOrWhiteSpace(UserAgent) ? "VLC/3.0.20 LibVLC/3.0.20" : UserAgent;

            async System.Threading.Tasks.Task Worker()
            {
                while (!ct.IsCancellationRequested && queue.TryDequeue(out var job))
                {
                    bool ok = false;
                    try
                    {
                        using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, job.url))
                        {
                            req.Headers.TryAddWithoutValidation("User-Agent", ua);
                            using (var resp = await ImageHttp.SendAsync(req, ct).ConfigureAwait(false))
                            {
                                var type = resp.Content.Headers.ContentType?.MediaType ?? "image";
                                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                if ((int)resp.StatusCode == 200 && bytes.Length >= 1000 && type.StartsWith("image", StringComparison.Ordinal))
                                {
                                    var full = Full(job.rel);
                                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                                    var tmp = full + ".tmp";
                                    File.WriteAllBytes(tmp, bytes);
                                    File.SetLastWriteTimeUtc(tmp, DateTimeOffset.FromUnixTimeSeconds(job.mtime).UtcDateTime);
                                    if (File.Exists(full)) File.Delete(full);
                                    File.Move(tmp, full);
                                    ok = true;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { _log.Debug($"Artwork {job.rel}: {ex.Message}"); }
                    lock (gate)
                    {
                        if (ok) { want[job.rel] = "img:" + job.url; saved++; } else failed++;
                        done++;
                        if (done % 500 == 0 || done == jobs.Count)
                        {
                            if (_status.Strm != null)
                                _status.Strm = new Dictionary<string, object>(_status.Strm) { ["art"] = new List<object> { done, (long)jobs.Count } };
                            Store.WriteJson(_manifestPath, want);
                        }
                        if (done % 5000 == 0) _log.Info($"Artwork: {done}/{jobs.Count}");
                        progress?.Invoke(done, jobs.Count);
                    }
                }
            }

            try
            {
                await System.Threading.Tasks.Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker())).ConfigureAwait(false);
            }
            finally
            {
                lock (gate) Store.WriteJson(_manifestPath, want);
                if (_status.Strm != null)
                {
                    var st = new Dictionary<string, object>(_status.Strm);
                    st.Remove("art");
                    st["images"] = saved;
                    st["written"] = Naming.ToInt(Json.Get(st, "written")) + saved;
                    st["files"] = (long)want.Count;
                    _status.Strm = st;
                }
                _downloads = new List<(string, string, long)>();
            }
            ct.ThrowIfCancellationRequested();
            _log.Info($"Artwork: {saved} saved, {failed} failed (tried again next refresh) in {(long)(DateTime.UtcNow - t0).TotalSeconds}s");
            return saved;
        }
    }
}
