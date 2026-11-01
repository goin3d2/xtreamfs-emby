using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XtreamFS.Engine
{
    /// <summary>What a refresh is doing right now, for the dashboard page.</summary>
    public sealed class Status
    {
        readonly object _lock = new object();
        public string Phase = "idle", Detail = "";
        public long Done, Total;
        public long? Eta;
        public long? Started;
        public Dictionary<string, object> Last;
        public string Shrink;
        public Dictionary<string, object> Strm;
        public readonly List<Dictionary<string, object>> Changes = new List<Dictionary<string, object>>();

        public Action<string, long, long> PhaseChanged; // for the scheduled task's progress bar

        public void SetPhase(string phase, string detail = "", long done = 0, long total = 0, long? eta = null)
        {
            lock (_lock) { Phase = phase; Detail = detail ?? ""; Done = done; Total = total; Eta = eta; }
            try { PhaseChanged?.Invoke(phase, done, total); } catch { }
        }

        public void AddChange(string kind, List<string> added, List<string> removed)
        {
            lock (_lock)
            {
                Changes.Add(new Dictionary<string, object>
                {
                    ["time"] = Naming.Now(), ["kind"] = kind, ["added"] = added, ["removed"] = removed,
                });
                while (Changes.Count > 20) Changes.RemoveAt(0);
            }
        }

        /// <summary>The parts worth keeping across Emby restarts.</summary>
        public Dictionary<string, object> Persisted()
        {
            lock (_lock)
                return new Dictionary<string, object>
                {
                    ["last"] = Last, ["shrink"] = Shrink, ["strm"] = Strm, ["changes"] = Changes.ToList(),
                };
        }

        public void Restore(object o)
        {
            if (!(o is Dictionary<string, object> d)) return;
            lock (_lock)
            {
                Last = Json.Get(d, "last") as Dictionary<string, object>;
                Shrink = Json.Get(d, "shrink") as string;
                Strm = Json.Get(d, "strm") as Dictionary<string, object>;
                if (Strm != null) Strm["running"] = false;
                Changes.Clear();
                if (Json.Get(d, "changes") is List<object> l)
                    foreach (var c in l) if (c is Dictionary<string, object> cd) Changes.Add(cd);
            }
        }

        public Dictionary<string, object> ToJson()
        {
            lock (_lock)
                return new Dictionary<string, object>
                {
                    ["phase"] = Phase, ["detail"] = Detail, ["done"] = Done, ["total"] = Total,
                    ["eta"] = Eta, ["started"] = Started, ["last"] = Last, ["shrink"] = Shrink,
                    ["strm"] = Strm, ["changes"] = Changes.ToList(),
                };
        }
    }

    /// <summary>Recent log lines for the dashboard, with the provider login masked.</summary>
    public sealed class LogRing
    {
        readonly object _lock = new object();
        readonly LinkedList<(long seq, string level, string text)> _lines = new LinkedList<(long, string, string)>();
        long _seq;
        readonly Func<string, string> _scrub;
        readonly Action<string, string> _forward; // (level, message) to Emby's own log

        public LogRing(Func<string, string> scrub, Action<string, string> forward)
        {
            _scrub = scrub; _forward = forward;
        }

        public void Info(string m) => Add("INFO", m);
        public void Warn(string m) => Add("WARNING", m);
        public void Error(string m) => Add("ERROR", m);
        /// <summary>Only to Emby's own log (at debug level), not the dashboard.</summary>
        public void Debug(string m) { try { _forward?.Invoke("DEBUG", _scrub(m ?? "")); } catch { } }

        void Add(string level, string message)
        {
            message = _scrub(message ?? "");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
            lock (_lock)
            {
                _seq++;
                _lines.AddLast((_seq, level, line));
                while (_lines.Count > 3000) _lines.RemoveFirst();
            }
            try { _forward?.Invoke(level, message); } catch { }
        }

        public (List<object> lines, long last) Since(long after, int limit = 1000)
        {
            lock (_lock)
            {
                var o = _lines.Where(x => x.seq > after).ToList();
                if (o.Count > limit) o = o.Skip(o.Count - limit).ToList();
                return (o.Select(x => (object)new List<object> { x.seq, x.level, _scrub(x.text) }).ToList(), _seq);
            }
        }
    }

    /// <summary>Small JSON files in the plugin's data folder, written atomically.</summary>
    public static class Store
    {
        public static object ReadJson(string path)
        {
            try { return File.Exists(path) ? Json.Parse(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        public static void WriteJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Write(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }

    /// <summary>
    /// What the last good refresh produced: counts and category names. Small, so
    /// the dashboard can show it without loading the whole catalog.
    /// </summary>
    public sealed class Summary
    {
        public long Fetched, Movies, Shows, Episodes, Files;
        public string SelectionKey = "";
        public Dictionary<string, string> MovieCategories = new Dictionary<string, string>();
        public Dictionary<string, string> SeriesCategories = new Dictionary<string, string>();
        public Dictionary<string, long> MovieCounts = new Dictionary<string, long>();
        public Dictionary<string, long> SeriesCounts = new Dictionary<string, long>();
        public Dictionary<string, long> MovieFolders = new Dictionary<string, long>();
        public Dictionary<string, long> SeriesFolders = new Dictionary<string, long>();

        public static Summary From(Catalog c, Tree t)
        {
            var s = new Summary
            {
                Fetched = c.Fetched, Movies = c.Movies.Count, Shows = c.Series.Count, Files = t.Files.Count,
                SelectionKey = c.SelectionKey, MovieCategories = c.MovieCategories, SeriesCategories = c.SeriesCategories,
                MovieCounts = c.MovieCounts, SeriesCounts = c.SeriesCounts,
            };
            foreach (var show in c.Series)
                foreach (var (_, eps) in TreeBuilder.EpisodeGroups(show)) s.Episodes += eps.Count;
            foreach (var m in c.Movies) Inc(s.MovieFolders, Json.S(Json.Get(m, "_folder")) is var f && f.Length > 0 ? f : "Movies");
            foreach (var x in c.Series) Inc(s.SeriesFolders, Json.S(Json.Get(x, "_folder")) is var f && f.Length > 0 ? f : "TV");
            return s;
        }

        static void Inc(Dictionary<string, long> d, string k) => d[k] = (d.TryGetValue(k, out var v) ? v : 0) + 1;

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["fetched"] = Fetched, ["movies"] = Movies, ["shows"] = Shows, ["episodes"] = Episodes, ["files"] = Files,
            ["selection"] = SelectionKey, ["movie_categories"] = MovieCategories, ["series_categories"] = SeriesCategories,
            ["counts"] = new Dictionary<string, object> { ["movie"] = MovieCounts, ["series"] = SeriesCounts },
            ["movie_folders"] = MovieFolders, ["series_folders"] = SeriesFolders,
        };

        public static Summary FromJson(object o)
        {
            if (!(o is Dictionary<string, object> d)) return null;
            Dictionary<string, string> SD(object x) =>
                (x as Dictionary<string, object>)?.ToDictionary(kv => kv.Key, kv => Json.S(kv.Value)) ?? new Dictionary<string, string>();
            Dictionary<string, long> LD(object x) =>
                (x as Dictionary<string, object>)?.ToDictionary(kv => kv.Key, kv => Naming.ToInt(kv.Value)) ?? new Dictionary<string, long>();
            return new Summary
            {
                Fetched = Naming.ToInt(Json.Get(d, "fetched")), Movies = Naming.ToInt(Json.Get(d, "movies")),
                Shows = Naming.ToInt(Json.Get(d, "shows")), Episodes = Naming.ToInt(Json.Get(d, "episodes")),
                Files = Naming.ToInt(Json.Get(d, "files")), SelectionKey = Json.S(Json.Get(d, "selection")),
                MovieCategories = SD(Json.Get(d, "movie_categories")), SeriesCategories = SD(Json.Get(d, "series_categories")),
                MovieCounts = LD(Json.Get(Json.Get(d, "counts"), "movie")), SeriesCounts = LD(Json.Get(Json.Get(d, "counts"), "series")),
                MovieFolders = LD(Json.Get(d, "movie_folders")), SeriesFolders = LD(Json.Get(d, "series_folders")),
            };
        }
    }
}
