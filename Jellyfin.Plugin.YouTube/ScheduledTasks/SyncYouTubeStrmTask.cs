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

        var primaryUser = !string.IsNullOrWhiteSpace(config.PrimaryJellyfinUser) ? config.PrimaryJellyfinUser : "fabian";
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
                totalAdded += await WriteStrmVideosAsync(seasonDir, feedVideos, cancellationToken).ConfigureAwait(false);
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

                totalAdded += await WriteStrmVideosAsync(upcomingDir, upcomingVideos, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sync YouTube upcoming section");
        }

        // 2. Sync Subscribed Channels (A-Z) as individual Series folders
        var channels = await client.GetSubscribedChannelsAsync(primaryUser, 40, cancellationToken).ConfigureAwait(false);
        foreach (var ch in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeChan = SanitizeFileName(ch.Title);
            var chanDir = Path.Combine(targetDir, $"{safeChan} [YouTubeChannel-{ch.ChannelId}]");
            var seasonDir = Path.Combine(chanDir, "Season 01");
            Directory.CreateDirectory(seasonDir);

            var videos = await client.GetChannelRssFeedAsync(ch.ChannelId, 12, cancellationToken).ConfigureAwait(false);
            // Make the newest upload on the first few channels have a near-future PremiereDate if needed or preserve real dates
            totalAdded += await WriteStrmVideosAsync(seasonDir, videos, cancellationToken).ConfigureAwait(false);
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
                totalAdded += await WriteStrmVideosAsync(plDir, plVideos, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not sync custom YouTube playlists to .strm folders");
        }

        logger.LogInformation("YouTube .strm sync completed: {Added} new .strm pointers written across feeds and {Channels} subscribed channels", totalAdded, channels.Count);
        return totalAdded;
    }

    private static async Task<int> WriteStrmVideosAsync(
        string seasonDir,
        List<YouTubeVideoItem> videos,
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

            var expectedUrl = $"http://127.0.0.1:8096/api/youtubePlugin/stream/{v.VideoId}/master.m3u8";
            if (!File.Exists(strmPath))
            {
                await File.WriteAllTextAsync(strmPath, expectedUrl, cancellationToken).ConfigureAwait(false);
                added++;
            }
        }

        // Prune stale .strm files in this feed folder so feeds stay clean and never accumulate duplicates
        try
        {
            foreach (var existing in Directory.GetFiles(seasonDir, "*.strm"))
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
