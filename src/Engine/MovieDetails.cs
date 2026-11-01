using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XtreamFS.Engine
{
    /// <summary>
    /// Full movie details from the provider (get_vod_info): one request per film, cached in
    /// movie_details.json. Same cache format and rules as the Docker version.
    /// </summary>
    public static class MovieDetails
    {
        static readonly string[] Fields = { "movie_image", "backdrop", "genre", "plot", "cast", "director",
            "releasedate", "duration_secs", "rating", "youtube_trailer", "mpaa" };
        public const long Retry = 7 * 86400; // retry a movie whose details failed after a week

        /// <summary>Cache key: the TMDB ID, so copies of a film in several categories share one
        /// lookup, or the stream ID when it has none.</summary>
        public static string Key(Dictionary<string, object> m)
        {
            var t = Json.S(Naming.TmdbOf(m)).Trim();
            return Naming.IsDigits(t) && t != "0" ? "t" + t : "s" + Json.S(Json.Get(m, "stream_id"));
        }

        static bool Empty(object v)
        {
            switch (v)
            {
                case null: return true;
                case string s: return s.Length == 0 || s == "0";
                case JNum n: return n.AsDouble() == 0;
                case bool b: return !b;
                default: return false;
            }
        }

        public static Dictionary<string, object> Trim(object info)
        {
            var o = new Dictionary<string, object>();
            if (!(info is Dictionary<string, object> d)) return o;
            foreach (var k in Fields)
            {
                var v = Json.Get(d, k);
                if (k == "backdrop" && !Json.Truthy(v)) v = Json.Get(d, "backdrop_path");
                if (k == "mpaa" && !Json.Truthy(v)) v = Json.Or(Json.Get(d, "mpaa_rating"), Json.Get(d, "age"));
                if (v is List<object> l)
                {
                    if (k == "backdrop" || k == "movie_image")
                    {
                        var first = l.Count > 0 ? Json.S(l[0]) : "";
                        var m = System.Text.RegularExpressions.Regex.Match(first, "https?://[^\\s'\"\\],]+");
                        v = m.Success ? m.Value : "";
                    }
                    else v = string.Join(", ", l.Select(x => Json.S(x).Trim()).Where(x => x.Length > 0));
                }
                if (v is Dictionary<string, object>) continue;
                if (!Empty(v)) o[k] = v;
            }
            return o;
        }

        /// <summary>The cached details for a movie, or null.</summary>
        public static Dictionary<string, object> Of(Dictionary<string, object> cache, Dictionary<string, object> m)
        {
            if (cache == null) return null;
            lock (cache) return Json.Get(Json.Get(cache, Key(m)), "i") as Dictionary<string, object>;
        }

        public static int Pending(Dictionary<string, object> cache, Catalog catalog)
        {
            lock (cache) return catalog.Movies.Select(Key).Distinct().Count(k => !cache.ContainsKey(k));
        }

        /// <summary>Fill the cache for every movie in the catalog that hasn't got details.</summary>
        public static async Task Fetch(XtreamClient xt, Catalog catalog, Dictionary<string, object> cache, Action save,
                                       Status status, LogRing log, bool stripPrefix, CancellationToken ct, int workers = 2)
        {
            var now = Naming.Now();
            var todo = new List<(string key, Dictionary<string, object> m)>();
            var keys = new HashSet<string>();
            lock (cache)
            {
                var queued = new HashSet<string>();
                foreach (var m in catalog.Movies)
                {
                    var k = Key(m);
                    keys.Add(k);
                    if (queued.Contains(k)) continue;
                    var c = Json.Get(cache, k) as Dictionary<string, object>;
                    if (c != null && (c.ContainsKey("i") || now - Naming.ToInt(Json.Get(c, "t")) < Retry)) continue;
                    queued.Add(k);
                    todo.Add((k, m));
                }
                // Forget films that are no longer in the catalog.
                foreach (var k in cache.Keys.Where(k => !keys.Contains(k)).ToList()) cache.Remove(k);
            }
            if (todo.Count == 0)
            {
                log.Info($"Movie details: all {keys.Count} cached");
                return;
            }
            var rate = Math.Max(0.05, xt.Delay + 0.25) / workers;
            log.Info($"Movie details: {todo.Count} of {keys.Count} films need details (about {Math.Round(todo.Count * rate / 60)} min)");

            var queue = new ConcurrentQueue<(string key, Dictionary<string, object> m)>(todo);
            var started = DateTime.UtcNow;
            var lastLog = started;
            int done = 0, failed = 0, streak = 0;
            var gate = new object();
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                status.SetPhase("details", "", 0, todo.Count, (long)(todo.Count * rate));

                async Task Worker()
                {
                    while (!stop.IsCancellationRequested && queue.TryDequeue(out var item))
                    {
                        Dictionary<string, object> entry;
                        try
                        {
                            var d = await xt.Api("get_vod_info", stop.Token, ("vod_id", Json.S(Json.Get(item.m, "stream_id")))).ConfigureAwait(false);
                            entry = new Dictionary<string, object> { ["t"] = Naming.Now(), ["i"] = Trim(Json.Get(d, "info")) };
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                        catch (Exception e)
                        {
                            log.Debug($"Movie details for {Json.S(Json.Get(item.m, "stream_id"))} ({Json.S(Json.Get(item.m, "name"))}): {e.Message}");
                            entry = new Dictionary<string, object> { ["t"] = Naming.Now(), ["err"] = 1L };
                        }
                        bool doSave = false;
                        lock (gate)
                        {
                            lock (cache) cache[item.key] = entry;
                            done++;
                            if (entry.ContainsKey("err"))
                            {
                                failed++;
                                if (++streak >= 50 && !stop.IsCancellationRequested)
                                {
                                    log.Warn("Movie details: 50 failures in a row; stopping for now (the rest are tried at the next refresh)");
                                    stop.Cancel();
                                }
                            }
                            else streak = 0;
                            if (done % 500 == 0) doSave = true;
                            var spent = (DateTime.UtcNow - started).TotalSeconds;
                            if (done % 10 == 0 || done == todo.Count)
                                status.SetPhase("details", Naming.CleanTitle(Json.Get(item.m, "name"), stripPrefix), done, todo.Count,
                                                (long)(spent / done * (todo.Count - done)));
                            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 60 || done == todo.Count)
                            {
                                log.Info($"Movie details: {done}/{todo.Count} ({done * 100 / todo.Count}%), about {Math.Round(spent / done * (todo.Count - done) / 60)} min left");
                                lastLog = DateTime.UtcNow;
                            }
                        }
                        if (doSave) lock (cache) save();
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Worker())).ConfigureAwait(false);
            }
            lock (cache) save();
            ct.ThrowIfCancellationRequested();
            log.Info($"Movie details: {done - failed} fetched, {failed} failed (retried in {Retry / 86400} days)");
        }
    }
}
