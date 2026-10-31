using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace XtreamFS.Tasks
{
    /// <summary>Re-reads the provider's catalog and updates the .strm folder.</summary>
    public class RefreshTask : IScheduledTask
    {
        readonly ILibraryManager _library;

        public RefreshTask(ILibraryManager libraryManager) { _library = libraryManager; }

        public string Name => "Refresh xtreamfs catalog";
        public string Key => "XtreamFSRefresh";
        public string Description =>
            "Reads the provider's movies and series for the selected categories and updates the .strm folder.";
        public string Category => "xtreamfs";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var engine = Plugin.Instance.Engine;
            engine.ScanLibrary = () => _library.QueueLibraryScan();
            return engine.RefreshAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(12).Ticks },
        };
    }
}
