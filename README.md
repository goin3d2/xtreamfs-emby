# xtreamfs for Emby

An Emby server plugin that turns an Xtream Codes provider's movies and series into a folder of `.strm` files (and optional `.nfo` files) that Emby libraries can scan. It is the Emby-only version of [xtreamfs](https://github.com/goin3d2/xtreamfs): no Docker, no FUSE mount, no `.env` file. Everything is set up and watched from a page in the Emby dashboard.

It writes the same folder layout and file names as xtreamfs, so you can switch between the two without Emby seeing new paths.

## Features

- **Dashboard page** (Dashboard → xtreamfs) with five tabs:
  - **Status**: a getting-started checklist, a **Refresh now** button, live progress with phase, counts and ETA, catalog totals, a folder table, the Emby libraries that use these folders, and the most recent changes.
  - **Categories**: pick movie and series categories from the provider's live list, exclude some, and give any category its own top-level folder (for example `Anime`).
  - **Settings**: the `.strm` folder (with a folder picker), folder layout, prefix stripping, `.nfo` writing and locking, the shrink guard, request delay, and whether to scan Emby libraries after a refresh.
  - **Account**: enter and test the provider login, then see account status, expiry and connection limits.
  - **Log**: a live log. Server addresses, usernames and passwords are masked.
- **Scheduled task** "Refresh xtreamfs catalog" runs every 12 hours by default. Change the schedule in Dashboard → Scheduled Tasks.
- **Shrink guard**: if the provider suddenly returns far fewer titles, the refresh stops before deleting files. **Accept the smaller library** on the Status tab lets the smaller catalog through.
- **Show copies are merged.** A 4K or Dolby Vision copy of a show goes into the same folder with a ` - 4K` / ` - 4K DV` label, so it isn't listed as a second show.
- **Stable paths.** A title keeps its path across refreshes, so Emby keeps its metadata and watch history.
- **Episode cache.** Only series whose episode list changed are fetched again.
- **Admin only.** All plugin API calls need an Emby administrator.

## Install

1. Download `XtreamFS.Emby.dll` from the [Releases](../../releases) page, or build it yourself (see below).
2. Copy it into Emby's `plugins` folder:
   - Docker: `/config/plugins` inside the container
   - Linux: `/var/lib/emby/plugins`
   - Windows: `%AppData%\Emby-Server\programdata\plugins`
3. Restart Emby.

Requires Emby Server 4.8 or newer.

## Setup

1. Open **Dashboard → xtreamfs**.
2. **Account** tab: enter the server URL (for example `http://provider.example:8080`), username and password, then select **Test connection**. Select **Save login** once the test passes.
3. **Settings** tab: choose the `.strm` folder. It must be a folder Emby can write to, for example `/media/xtreamfs`.
4. **Categories** tab: tick the movie and series categories you want, then save. Saving starts a refresh.
5. Watch the progress on the **Status** tab. The first refresh reads every episode list, so it can take a while on a large catalog.
6. In **Dashboard → Libraries**, add libraries pointing at the folders it created, for example a Movies library on `<strm folder>/Movies` and a TV Shows library on `<strm folder>/TV`.

### Tips

- Keep TV categories out of category folders unless each category is its own library. Otherwise Emby can treat category folders as shows.
- The provider doesn't supply stream details, so Emby fills in media info the first time an item is played.
- `.nfo` files give Emby titles, plots, years and TMDB ids without a lookup. **Lock** adds `<lockdata>` so Emby won't overwrite them.

## Build

The plugin targets `netstandard2.1` and needs no NuGet packages. It is compiled against the `MediaBrowser.*` assemblies from an Emby server.

1. Install the .NET SDK (6 or newer).
2. Copy these files from an Emby Server 4.8+ install (or its release zip, `system/` folder) into `emby-sdk/`:
   - `MediaBrowser.Common.dll`
   - `MediaBrowser.Controller.dll`
   - `MediaBrowser.Model.dll`
3. Build:

   ```sh
   dotnet build -c Release -o out
   ```

   Pass `-p:EmbySdkDir=/path/to/dlls` to use another folder.

The output is `out/XtreamFS.Emby.dll`. The `nuget.config` clears package sources so the build works offline.

## Releases

Releases are published by GitHub Actions (`.github/workflows/release.yml`). When a push to `main` changes `<Version>` in `XtreamFS.Emby.csproj`, the workflow builds the plugin and publishes `v<version>` with `XtreamFS.Emby.dll` attached. It can also be run by hand from **Actions → Release → Run workflow**. A version that already has a release is skipped.

## How it relates to xtreamfs

| | xtreamfs (Docker) | xtreamfs for Emby |
|---|---|---|
| Plex (FUSE mount) | Yes | No |
| Emby `.strm` folder | Yes | Yes |
| Settings | `.env` plus the web page on port 8099 | Emby dashboard |
| Schedule | `REFRESH_HOURS` | Emby Scheduled Tasks |
| Folder layout and file names | Same | Same |

## Files

The plugin keeps its state in Emby's data folder, under `data/xtreamfs/`:

| File | Contents |
|---|---|
| `state.json` | Assigned paths and first-seen times |
| `summary.json` | Catalog totals |
| `episodes.json` | Episode-list cache |
| `strm_manifest.json` | Files the plugin wrote |
| `status.json` | Last refresh status |
| `categories.json` | Provider categories and the titles in each, by Emby path and TMDB id (read by Emby Collections) ; a copy is also written to the top of the `.strm` folder |

Deleting `episodes.json` makes the next refresh re-read every episode list. Deleting `state.json` lets paths be reassigned, which Emby will see as new items.
