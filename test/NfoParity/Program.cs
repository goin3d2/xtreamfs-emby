// Compares movie .nfo output and detail trimming with the Docker version (xtreamfs.py).
// Usage: NfoParity <input.json> <output.json>; input is a list of {"m": movie, "raw": get_vod_info "info"}.
using System.Collections.Generic;
using System.IO;
using XtreamFS.Engine;

var input = (List<object>)Json.Parse(File.ReadAllText(args[0]));
var o = new List<object>();
foreach (var it in input)
{
    var m = (Dictionary<string, object>)Json.Get(it, "m");
    var trim = MovieDetails.Trim(Json.Get(it, "raw"));
    var trimmed = (Dictionary<string, object>)Json.Parse(Json.Write(trim)); // as reloaded from the cache file
    var (poster, fanart) = Nfo.MovieArt(m, trimmed);
    o.Add(new Dictionary<string, object>
    {
        ["key"] = MovieDetails.Key(m),
        ["trim"] = trim,
        ["nfo"] = Nfo.Movie(m, true, trimmed),
        ["nfo_plain"] = Nfo.Movie(m, false),
        ["art"] = new List<object> { poster, fanart, poster.Length > 0 ? Nfo.ImageExt(poster) : "", fanart.Length > 0 ? Nfo.ImageExt(fanart) : "" },
    });
}
File.WriteAllText(args[1], Json.Write(o));
