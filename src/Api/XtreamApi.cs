using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using XtreamFS.Engine;
using XtreamFS.Tasks;

namespace XtreamFS.Api
{
    [Route("/xtreamfs/status", "GET", Summary = "Refresh progress and library summary")]
    [Authenticated(Roles = "admin")]
    public class GetStatus : IReturn<object> { }

    [Route("/xtreamfs/log", "GET", Summary = "Recent log lines")]
    [Authenticated(Roles = "admin")]
    public class GetLog : IReturn<object> { public long After { get; set; } }

    [Route("/xtreamfs/categories", "GET", Summary = "Provider categories and the current selection")]
    [Authenticated(Roles = "admin")]
    public class GetCategories : IReturn<object> { public bool Live { get; set; } }

    [Route("/xtreamfs/account", "GET", Summary = "Provider account details")]
    [Authenticated(Roles = "admin")]
    public class GetAccount : IReturn<object> { public bool Fresh { get; set; } }

    [Route("/xtreamfs/login/test", "POST", Summary = "Check a provider login without saving it")]
    [Authenticated(Roles = "admin")]
    public class TestLogin : IReturn<object>
    {
        public string ServerUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    [Route("/xtreamfs/refresh", "POST", Summary = "Start a catalog refresh")]
    [Authenticated(Roles = "admin")]
    public class StartRefresh : IReturn<object> { }

    [Route("/xtreamfs/acceptshrink", "POST", Summary = "Accept a smaller catalog the shrink guard held back")]
    [Authenticated(Roles = "admin")]
    public class AcceptShrink : IReturn<object> { }

    public class XtreamApi : IService, IRequiresRequest
    {
        readonly ITaskManager _tasks;
        readonly IHttpResultFactory _results;
        readonly ILibraryManager _library;
        public IRequest Request { get; set; }

        public XtreamApi(ITaskManager taskManager, IHttpResultFactory resultFactory, ILibraryManager libraryManager)
        {
            _tasks = taskManager;
            _results = resultFactory;
            _library = libraryManager;
        }

        /// <summary>Emby libraries with a folder inside the .strm folder.</summary>
        List<object> StrmLibraries(string root)
        {
            var o = new List<object>();
            if (string.IsNullOrWhiteSpace(root)) return o;
            root = root.TrimEnd('/', '\\');
            try
            {
                foreach (var f in _library.GetVirtualFolders())
                {
                    var hits = (f.Locations ?? new string[0])
                        .Where(l => l != null && (l.TrimEnd('/', '\\') == root || l.StartsWith(root + "/") || l.StartsWith(root + "\\")))
                        .ToList();
                    if (hits.Count > 0)
                        o.Add(new Dictionary<string, object> { ["name"] = f.Name, ["locations"] = hits });
                }
            }
            catch { }
            return o;
        }

        static XtreamEngine Engine => Plugin.Instance.Engine;

        object JsonResult(object value) =>
            _results.GetResult(Request, Json.Write(value).AsSpan(), "application/json", new Dictionary<string, string>());

        IScheduledTaskWorker Worker() => _tasks.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is RefreshTask);

        public object Get(GetStatus request)
        {
            var c = Plugin.Instance.Configuration;
            var w = Worker();
            long? next = null, last = null;
            string lastResult = null;
            if (w != null)
            {
                var r = w.LastExecutionResult;
                if (r != null)
                {
                    last = r.EndTimeUtc.ToUnixTimeSeconds();
                    lastResult = r.Status.ToString();
                }
                var interval = w.Triggers?.Where(t => t.Type == TaskTriggerInfo.TriggerInterval && t.IntervalTicks.HasValue)
                    .Select(t => t.IntervalTicks.Value).DefaultIfEmpty(0).Min() ?? 0;
                if (interval > 0)
                    next = (last ?? Naming.Now()) + (long)TimeSpan.FromTicks(interval).TotalSeconds;
            }
            var s = Engine.Summary;
            return JsonResult(new Dictionary<string, object>
            {
                ["now"] = Naming.Now(),
                ["status"] = Engine.Status.ToJson(),
                ["running"] = Engine.IsRunning || (w != null && w.State == TaskState.Running),
                ["catalog"] = s?.ToJson(),
                ["next_at"] = next,
                ["task_last_result"] = lastResult,
                ["strm_folder"] = c.StrmFolder,
                ["libraries"] = StrmLibraries(c.StrmFolder),
                ["has_login"] = !string.IsNullOrWhiteSpace(c.ServerUrl) && !string.IsNullOrWhiteSpace(c.Username)
                                && !string.IsNullOrEmpty(c.Password),
            });
        }

        public object Get(GetLog request)
        {
            var (lines, last) = Engine.Log.Since(request.After);
            return JsonResult(new Dictionary<string, object> { ["last"] = last, ["lines"] = lines });
        }

        public async Task<object> Get(GetCategories request)
        {
            try { return JsonResult(await Engine.Categories(request.Live).ConfigureAwait(false)); }
            catch (Exception e) { return JsonResult(new Dictionary<string, object> { ["error"] = Engine.Scrub(e.Message) }); }
        }

        public async Task<object> Get(GetAccount request) =>
            JsonResult(await Engine.Account(request.Fresh).ConfigureAwait(false));

        public async Task<object> Post(TestLogin request)
        {
            var c = Plugin.Instance.Configuration;
            var password = string.IsNullOrEmpty(request.Password) ? c.Password : request.Password;
            return JsonResult(await Engine.CheckLogin(request.ServerUrl, request.Username, password, c.UserAgent).ConfigureAwait(false));
        }

        public object Post(StartRefresh request)
        {
            if (Engine.IsRunning) return JsonResult(new Dictionary<string, object> { ["ok"] = false, ["error"] = "A refresh is already running." });
            Engine.ForgetAccount();
            _tasks.QueueScheduledTask<RefreshTask>();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }

        public object Post(AcceptShrink request)
        {
            Engine.AcceptShrinkOnce = true;
            if (!Engine.IsRunning) _tasks.QueueScheduledTask<RefreshTask>();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }
    }
}
