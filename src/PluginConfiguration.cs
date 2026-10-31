using MediaBrowser.Model.Plugins;

namespace XtreamFS
{
    /// <summary>
    /// Settings, saved by Emby as XML. Category, folder and override fields use
    /// the same formats as the xtreamfs container's .env settings.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Provider login
        public string ServerUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string UserAgent { get; set; } = "VLC/3.0.20 LibVLC/3.0.20";

        // Where the .strm copy goes: a folder Emby can read and write.
        public string StrmFolder { get; set; } = "";

        // Categories: IDs or names, comma-separated, or "all".
        public string MovieCategories { get; set; } = "";
        public string SeriesCategories { get; set; } = "";
        public string MovieExclude { get; set; } = "";
        public string SeriesExclude { get; set; } = "";
        // Categories with their own top-level folder: "927=Anime Movies,874=4K Movies".
        public string MovieFolders { get; set; } = "";
        public string SeriesFolders { get; set; } = "";
        // Titles sent to a folder by TMDB ID: "46260,31910=Anime;1234=Kids".
        public string MovieOverrides { get; set; } = "";
        public string SeriesOverrides { get; set; } = "";

        // Layout
        public bool MovieCategoryFolders { get; set; }
        public bool SeriesCategoryFolders { get; set; }
        public bool StripPrefix { get; set; } = true;

        // Refresh behaviour
        public double ApiDelay { get; set; } = 0.3;
        public double ShrinkGuardPercent { get; set; } = 25;
        public long ShrinkGuardMin { get; set; } = 50;
        public bool ScanLibraryAfterRefresh { get; set; }

        // Metadata files
        public bool WriteNfo { get; set; }
        public bool LockNfo { get; set; }
    }
}
