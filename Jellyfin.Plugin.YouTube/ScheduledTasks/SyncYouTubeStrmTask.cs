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
            totalAdded += await WriteStrmVideosAsync(seasonDir, videos, cancellationToken).ConfigureAwait(false);
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
        var epNum = 1;
        foreach (var v in videos)
        {
            if (string.IsNullOrWhiteSpace(v.VideoId))
            {
                continue;
            }

            var prefix = !string.IsNullOrWhiteSpace(v.ChannelTitle) && !v.Title.Contains(v.ChannelTitle, StringComparison.OrdinalIgnoreCase)
                ? $"{v.ChannelTitle} - {v.Title}"
                : v.Title;
            var safeTitle = SanitizeFileName(prefix);
            var strmPath = Path.Combine(seasonDir, $"S01E{epNum:D2} - {safeTitle} [YouTubeVideo-{v.VideoId}].strm");
            epNum++;

            var expectedUrl = $"http://127.0.0.1:8096/api/youtubePlugin/stream/{v.VideoId}/master.m3u8";
            if (!File.Exists(strmPath))
            {
                await File.WriteAllTextAsync(strmPath, expectedUrl, cancellationToken).ConfigureAwait(false);
                added++;
            }
        }

        return added;
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
