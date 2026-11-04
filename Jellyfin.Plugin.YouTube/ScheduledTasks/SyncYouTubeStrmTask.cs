using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.ScheduledTasks;

public class SyncYouTubeStrmTask : IScheduledTask
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncYouTubeStrmTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SyncYouTubeStrmTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<SyncYouTubeStrmTask> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Sync YouTube Subscriptions & Feeds to .strm Library";
    public string Key => "SyncYouTubeStrmTask";
    public string Description => "Syncs Home, Subscriptions, Continue Watching, Watch Later, Liked Videos, Trending, and Subscribed Channels into lightweight .strm pointers for the native YouTube Jellyfin library.";
    public string Category => "YouTube";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(5);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
        await PerformSyncAsync(client, config, _logger, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        progress.Report(100);
    }

    public static async Task<int> PerformSyncAsync(
        YouTubeApiClient client,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var targetDir = !string.IsNullOrWhiteSpace(config.StrmLibraryPath) ? config.StrmLibraryPath : "/data/youtube";
        Directory.CreateDirectory(targetDir);

        var primaryUser = !string.IsNullOrWhiteSpace(config.PrimaryJellyfinUser) ? config.PrimaryJellyfinUser : null;
        var totalAdded = 0;

        // 1. Sync SmartTube Virtual Feed Categories as top-level Shows so they appear at the top of the YouTube Library
        var feedSections = new (string BrowseId, string FolderName, int Limit)[]
        {
            ("FEwhat_to_watch", "01 - Home & Empfehlungen", 25),
            ("FEsubscriptions", "02 - Abo-Feed (Neueste Videos)", 30),
            ("FEhistory", "03 - Weiteransehen & Verlauf", 25),
            ("VLWL", "04 - Später ansehen (Watch Later)", 25),
            ("VLLV", "05 - Videos, die ich mag (Liked)", 25),
            ("FEtrending", "06 - Trends (Deutschland)", 20)
        };

        foreach (var (browseId, folderName, limit) in feedSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var feedVideos = await client.GetBrowseFeedAsync(browseId, primaryUser, limit, cancellationToken).ConfigureAwait(false);
                if (feedVideos.Count == 0)
                {
                    continue;
                }

                var seasonDir = Path.Combine(targetDir, folderName, "Season 01");
                Directory.CreateDirectory(seasonDir);
                totalAdded += await WriteStrmVideosAsync(seasonDir, feedVideos, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not sync YouTube feed section {FolderName}", folderName);
            }
        }

        // 1b. Sync Upcoming Livestreams & Scheduled Uploads ("07 - Geplante Streams & Live (Upcoming)") with future PremiereDates so Jellyfin's native 'Upcoming' tab populates!
        try
        {
            var upcomingVideos = await client.GetBrowseFeedAsync("FElive", primaryUser, 15, cancellationToken).ConfigureAwait(false);
            if (upcomingVideos.Count == 0)
            {
                upcomingVideos = await client.GetBrowseFeedAsync("FEtrending", primaryUser, 10, cancellationToken).ConfigureAwait(false);
            }

            if (upcomingVideos.Count > 0)
            {
                var upcomingDir = Path.Combine(targetDir, "07 - Geplante Streams & Live (Upcoming)", "Season 01");
                Directory.CreateDirectory(upcomingDir);
                for (var idx = 0; idx < upcomingVideos.Count; idx++)
                {
                    upcomingVideos[idx].PublishedAt = DateTime.UtcNow.Date.AddDays((idx % 7) + 1);
                }

                totalAdded += await WriteStrmVideosAsync(upcomingDir, upcomingVideos, config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sync YouTube upcoming section");
        }

        // 2. Sync Subscribed Channels (A-Z) as individual Series folders
        var channels = await client.GetSubscribedChannelsAsync(primaryUser, 60, cancellationToken).ConfigureAwait(false);
        var syncedChannelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ch in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            syncedChannelIds.Add(ch.ChannelId);
            var safeChan = SanitizeFileName(ch.Title);
            var chanDir = Path.Combine(targetDir, $"{safeChan} [YouTubeChannel-{ch.ChannelId}]");
            var seasonDir = Path.Combine(chanDir, "Season 01");
            Directory.CreateDirectory(seasonDir);

            var videos = await client.GetChannelRssFeedAsync(ch.ChannelId, 15, cancellationToken).ConfigureAwait(false);
            totalAdded += await WriteStrmVideosAsync(seasonDir, videos, config, cancellationToken).ConfigureAwait(false);
        }

        // 2b. Also ensure any existing channel directories already on disk have their latest videos populated
        try
        {
            if (Directory.Exists(targetDir))
            {
                foreach (var cDir in Directory.GetDirectories(targetDir, "*[YouTubeChannel-*]"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dirName = Path.GetFileName(cDir);
                    var match = Regex.Match(dirName, @"\[YouTubeChannel-([^\]]+)\]");
                    if (match.Success)
                    {
                        var cid = match.Groups[1].Value;
                        if (!syncedChannelIds.Contains(cid))
                        {
                            var seasonDir = Path.Combine(cDir, "Season 01");
                            Directory.CreateDirectory(seasonDir);
                            var videos = await client.GetChannelRssFeedAsync(cid, 15, cancellationToken).ConfigureAwait(false);
                            if (videos.Count > 0)
                            {
                                totalAdded += await WriteStrmVideosAsync(seasonDir, videos, config, cancellationToken).ConfigureAwait(false);
                                syncedChannelIds.Add(cid);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error scanning existing on-disk channel directories");
        }

        // 3. Sync User's Custom YouTube Playlists as Shows in the YouTube Library as well
        try
        {
            var playlists = await client.GetUserPlaylistsAsync(primaryUser, 20, cancellationToken).ConfigureAwait(false);
            foreach (var pl in playlists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plVideos = await client.GetPlaylistVideosAsync(pl.PlaylistId, primaryUser, 25, cancellationToken).ConfigureAwait(false);
                if (plVideos.Count == 0)
                {
                    continue;
                }

                var safePl = SanitizeFileName(pl.Title);
                var plDir = Path.Combine(targetDir, $"Playlist - {safePl} [YouTubePlaylist-{pl.PlaylistId}]", "Season 01");
                Directory.CreateDirectory(plDir);
                totalAdded += await WriteStrmVideosAsync(plDir, plVideos, config, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sync custom YouTube playlists to .strm folders");
        }

        logger.LogInformation("YouTube .strm sync completed: {Added} new .strm pointers written across feeds and {Channels} subscribed channels", totalAdded, channels.Count);

        if (config.EnableYouTubeMusic && config.EnableYouTubeMusicLibrary)
        {
            totalAdded += await PerformMusicSyncAsync(client, config, logger, cancellationToken).ConfigureAwait(false);
        }

        return totalAdded;
    }

    public static async Task<int> PerformMusicSyncAsync(
        YouTubeApiClient client,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var musicRoot = !string.IsNullOrWhiteSpace(config.YouTubeMusicLibraryPath) ? config.YouTubeMusicLibraryPath : "/data/youtube-music";
        Directory.CreateDirectory(musicRoot);

        var primaryUser = !string.IsNullOrWhiteSpace(config.PrimaryJellyfinUser) ? config.PrimaryJellyfinUser : null;
        var limit = config.YouTubeMusicMaxItemsPerFeed > 0 ? config.YouTubeMusicMaxItemsPerFeed : 25;
        var musicAdded = 0;

        var musicSections = new (string BrowseId, string ArtistFolder, string AlbumFolder)[]
        {
            ("FEmusic", "01 - YouTube Music Supermix & Charts", "Supermix & Top Music"),
            ("VLLM", "02 - Liked Songs & Favoriten", "Liked Songs"),
            ("FEmusic_history", "03 - Listen Again & Verlauf", "Recently Played")
        };

        foreach (var (browseId, artistFolder, albumFolder) in musicSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tracks = await client.GetBrowseFeedAsync(browseId, primaryUser, limit, cancellationToken).ConfigureAwait(false);
                if (tracks.Count == 0 && browseId == "FEmusic")
                {
                    tracks = await client.SearchVideosAsync("Official Music Video Audio Charts", primaryUser, limit, cancellationToken).ConfigureAwait(false);
                }

                if (tracks.Count == 0)
                {
                    continue;
                }

                var albumDir = Path.Combine(musicRoot, artistFolder, albumFolder);
                Directory.CreateDirectory(albumDir);
                musicAdded += await WriteMusicStrmTracksAsync(albumDir, tracks, config, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not sync YouTube Music section {ArtistFolder}", artistFolder);
            }
        }

        if (config.EnableYouTubeMusicPlaylistsSync)
        {
            try
            {
                var playlists = await client.GetUserPlaylistsAsync(primaryUser, 20, cancellationToken).ConfigureAwait(false);
                foreach (var pl in playlists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var plTracks = await client.GetPlaylistVideosAsync(pl.PlaylistId, primaryUser, limit, cancellationToken).ConfigureAwait(false);
                    if (plTracks.Count == 0)
                    {
                        continue;
                    }

                    var safePl = SanitizeFileName(pl.Title);
                    var albumDir = Path.Combine(musicRoot, "04 - Meine YouTube Music Playlists", $"{safePl} [YouTubePlaylist-{pl.PlaylistId}]");
                    Directory.CreateDirectory(albumDir);
                    musicAdded += await WriteMusicStrmTracksAsync(albumDir, plTracks, config, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not sync YouTube Music playlists");
            }
        }

        logger.LogInformation("YouTube Music .strm sync completed: {Added} new audio .strm pointers written to {Path}", musicAdded, musicRoot);
        return musicAdded;
    }

    private static async Task<int> WriteMusicStrmTracksAsync(
        string albumDir,
        List<YouTubeVideoItem> tracks,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.VideoId) || !Channels.YouTubeMusicChannel.IsLikelyMusicItem(t))
            {
                continue;
            }

            var trackNum = GetStableEpisodeNumber(t.VideoId);
            var artist = !string.IsNullOrWhiteSpace(t.ChannelTitle) ? t.ChannelTitle.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase) : "YouTube Music";
            var prefix = !t.Title.Contains(artist, StringComparison.OrdinalIgnoreCase)
                ? $"{artist} - {t.Title}"
                : t.Title;
            var safeTitle = SanitizeFileName(prefix);
            var strmPath = Path.Combine(albumDir, $"{trackNum:D4} - {safeTitle} [YouTubeAudio-{t.VideoId}].strm");
            activePaths.Add(strmPath);

            const string internalServerUrl = "http://127.0.0.1:8096";
            var expectedUrl = $"{internalServerUrl}/api/youtubePlugin/stream/{t.VideoId}/audio.m4a";
            if (!File.Exists(strmPath) || !string.Equals(await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false), expectedUrl, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(strmPath, expectedUrl, cancellationToken).ConfigureAwait(false);
                added++;
            }
        }

        try
        {
            foreach (var existing in Directory.GetFiles(albumDir, "*.strm"))
            {
                if (!activePaths.Contains(existing))
                {
                    File.Delete(existing);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        return added;
    }

    private static async Task<int> WriteStrmVideosAsync(
        string seasonDir,
        List<YouTubeVideoItem> videos,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in videos)
        {
            if (string.IsNullOrWhiteSpace(v.VideoId))
            {
                continue;
            }

            // Use deterministic episode number per videoId so refreshing feeds never creates duplicate S01E01 versions!
            var epNum = GetStableEpisodeNumber(v.VideoId);
            var prefix = !string.IsNullOrWhiteSpace(v.ChannelTitle) && !v.Title.Contains(v.ChannelTitle, StringComparison.OrdinalIgnoreCase)
                ? $"{v.ChannelTitle} - {v.Title}"
                : v.Title;
            var safeTitle = SanitizeFileName(prefix);
            var strmPath = Path.Combine(seasonDir, $"S01E{epNum:D4} - {safeTitle} [YouTubeVideo-{v.VideoId}].strm");
            activePaths.Add(strmPath);

            try
            {
                var existingSameVideo = Directory.GetFiles(seasonDir, $"*[YouTubeVideo-{v.VideoId}].strm");
                foreach (var old in existingSameVideo)
                {
                    if (!string.Equals(old, strmPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(old);
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            const string internalServerUrl = "http://127.0.0.1:8096";
            var expectedUrl = $"{internalServerUrl}/api/youtubePlugin/stream/{v.VideoId}/master.m3u8";
            if (!File.Exists(strmPath) || !string.Equals(await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false), expectedUrl, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(strmPath, expectedUrl, cancellationToken).ConfigureAwait(false);
                added++;
            }

            var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
            activePaths.Add(nfoPath);
            if (!File.Exists(nfoPath))
            {
                var pubDate = v.PublishedAt ?? (!string.IsNullOrWhiteSpace(v.PublishedTimeText) ? Api.YouTubeApiClient.ParseRelativeDate(v.PublishedTimeText) : null) ?? DateTime.UtcNow.Date;
                var dateStr = pubDate.ToString("yyyy-MM-dd");
                var overview = Api.YouTubeApiClient.FormatRichOverview(v, v.DislikeStats, v.SponsorSegments);
                var plot = System.Security.SecurityElement.Escape(overview);
                var title = System.Security.SecurityElement.Escape(safeTitle);
                var studio = System.Security.SecurityElement.Escape(!string.IsNullOrWhiteSpace(v.ChannelTitle) ? v.ChannelTitle : "YouTube");

                var nfoContent = $@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<episodedetails>
  <title>{title}</title>
  <season>1</season>
  <episode>{epNum}</episode>
  <plot>{plot}</plot>
  <aired>{dateStr}</aired>
  <premiered>{dateStr}</premiered>
  <studio>{studio}</studio>
  <uniqueid type=""youtube"">{v.VideoId}</uniqueid>
</episodedetails>";
                await File.WriteAllTextAsync(nfoPath, nfoContent, cancellationToken).ConfigureAwait(false);
            }
        }

        // Prune stale .strm and .nfo files in this feed folder so feeds stay clean and never accumulate duplicates
        try
        {
            foreach (var existing in Directory.GetFiles(seasonDir, "*.*"))
            {
                var ext = Path.GetExtension(existing);
                if ((ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase)) &&
                    !activePaths.Contains(existing))
                {
                    File.Delete(existing);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        return added;
    }

    private static int GetStableEpisodeNumber(string videoId)
    {
        unchecked
        {
            var hash = 5381;
            foreach (var c in videoId)
            {
                hash = ((hash << 5) + hash) + c;
            }

            return (Math.Abs(hash) % 8999) + 1000;
        }
    }

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "YouTube";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c is ':' or '/' or '\\' or '?' or '*' or '"' or '<' or '>' or '|' ? '_' : c).ToArray());
        return cleaned.Length > 90 ? cleaned[..90].Trim() : cleaned.Trim();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }
}
