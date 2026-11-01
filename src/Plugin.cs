using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using XtreamFS.Engine;

namespace XtreamFS
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public XtreamEngine Engine { get; }

        static readonly Guid PluginId = new Guid("6c2f0d5e-8a3b-4b7e-9c41-2f5d8e7a1b30");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            var logger = logManager.GetLogger("xtreamfs");
            Engine = new XtreamEngine(Path.Combine(applicationPaths.DataPath, "xtreamfs"), () => Configuration,
                (level, message) =>
                {
                    // Pass the text as an argument so braces in titles aren't read as format codes.
                    if (level == "ERROR") logger.Error("{0}", message);
                    else if (level == "WARNING") logger.Warn("{0}", message);
                    else if (level == "DEBUG") logger.Debug("{0}", message);
                    else logger.Info("{0}", message);
                });
        }

        public override string Name => "xtreamfs";
        public override Guid Id => PluginId;
        public override string Description =>
            "Turns an Xtream Codes IPTV provider's movies and series into a .strm library for Emby, " +
            "with stable folder names, category folders and a live status page.";

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "xtreamfs", DisplayName = "xtreamfs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.xtreamfs.html",
                EnableInMainMenu = true, MenuIcon = "movie", IsMainConfigPage = true,
            },
            new PluginPageInfo
            {
                Name = "xtreamfsjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.xtreamfs.js",
            },
        };

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Web.thumb.png");
        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
