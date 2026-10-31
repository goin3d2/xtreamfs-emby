// Builds the tree for a catalog + state with the C# engine and prints it as JSON,
// so it can be compared with the Python implementation. Also writes .strm/.nfo.
using System; using System.Collections.Generic; using System.IO; using System.Linq; using XtreamFS.Engine;
var cat = (Dictionary<string, object>)Json.Parse(File.ReadAllText(args[0]));
var state = BuildState.FromJson(Json.Parse(File.ReadAllText(args[1])));
var c = new Catalog();
c.Movies = ((List<object>)cat["movies"]).Cast<Dictionary<string, object>>().ToList();
c.Series = ((List<object>)cat["series"]).Cast<Dictionary<string, object>>().ToList();
var cats = (Dictionary<string, object>)cat["categories"];
c.MovieCategories = ((Dictionary<string, object>)cats["movie"]).ToDictionary(kv => kv.Key, kv => Json.S(kv.Value));
c.SeriesCategories = ((Dictionary<string, object>)cats["series"]).ToDictionary(kv => kv.Key, kv => Json.S(kv.Value));
var xt = new XtreamClient("http://p", "u", "pw", null, 0);
var o = new BuildOptions { MovieLayout = args[2], SeriesLayout = args[3], MovieUrl = xt.MovieUrl, EpisodeUrl = xt.EpisodeUrl, Now = 1000,
  SeriesOverrides = Selection.Overrides(args.Length > 5 ? args[5] : ""), MovieOverrides = Selection.Overrides(args.Length > 6 ? args[6] : "") };
var (tree, ns) = TreeBuilder.Build(c, state, o);
var files = tree.Files.ToDictionary(kv => kv.Key, kv => (object)new List<object> { kv.Value.Url, kv.Value.Key, kv.Value.Mtime });
File.WriteAllText(args[4], Json.Write(new Dictionary<string, object> { ["files"] = files, ["state"] = ns.ToJson() }));
if (args.Length > 7) {
  var log = new LogRing(s => s, null); var st = new Status();
  new StrmWriter(args[7], Path.GetDirectoryName(args[4]), log, st) { Nfo = true }.Sync(tree);
  Console.WriteLine(Json.Write(st.Strm));
}
Console.WriteLine($"{tree.Files.Count} files");
