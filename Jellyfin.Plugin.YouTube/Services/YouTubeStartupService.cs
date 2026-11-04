using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Services;

/// <summary>
/// Startup &amp; background service for YouTube OAuth, script injection, and library artwork.
/// </summary>
public class YouTubeStartupService : IHostedService
{
    private readonly IServerApplicationPaths _appPaths;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeStartupService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private static readonly ConcurrentDictionary<string, DateTime> _recentSkips = new();

    public YouTubeStartupService(
        IServerApplicationPaths appPaths,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeStartupService> logger,
        ILoggerFactory loggerFactory)
    {
        _appPaths = appPaths;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TryBootstrapSeedAccount();
        TryInjectIndexHtmlScript();
        await EnsureYouTubeLibraryAsync().ConfigureAwait(false);
        await EnsureYouTubeMusicLibraryAsync().ConfigureAwait(false);

        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _userDataManager.UserDataSaved += OnUserDataSaved;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void TryBootstrapSeedAccount()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            var seedFile = File.Exists("/config/data/plugins/YouTube/oauth_seed.json")
                ? "/config/data/plugins/YouTube/oauth_seed.json"
                : (File.Exists("/config/data/plugins/YouTube/fabian_oauth_seed.json") ? "/config/data/plugins/YouTube/fabian_oauth_seed.json" : null);

            if (seedFile != null)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(seedFile));
                var root = doc.RootElement;
                var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty;
                var clientSecret = root.TryGetProperty("clientSecret", out var cs) ? cs.GetString() ?? string.Empty : string.Empty;
                var refreshToken = root.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
                var accountName = root.TryGetProperty("accountName", out var an) ? an.GetString() ?? "Primary Account" : "Primary Account";
                var targetUser = root.TryGetProperty("jellyfinUser", out var ju) ? ju.GetString() : null;
                if (string.IsNullOrWhiteSpace(targetUser))
                {
                    targetUser = config.PrimaryJellyfinUser;
                }

                if (!string.IsNullOrWhiteSpace(targetUser))
                {
                    config.PrimaryJellyfinUser = targetUser;
                }

                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    if (string.IsNullOrWhiteSpace(config.PersistedRefreshToken))
                    {
                        config.PersistedRefreshToken = refreshToken;
                    }

                    if (!string.IsNullOrWhiteSpace(clientId))
                    {
                        config.OAuthClientId = clientId;
                    }

                    if (!string.IsNullOrWhiteSpace(clientSecret))
                    {
                        config.OAuthClientSecret = clientSecret;
                    }

                    config.UserAccounts ??= new List<YouTubeUserAccountEntry>();
                    var userEntry = config.UserAccounts.FirstOrDefault(u =>
                        string.Equals(u.JellyfinUser, targetUser, StringComparison.OrdinalIgnoreCase));
                    if (userEntry == null)
                    {
                        userEntry = new YouTubeUserAccountEntry
                        {
                            JellyfinUser = targetUser,
                            JellyfinUserId = config.PrimaryJellyfinUserId ?? string.Empty,
                            AccountName = accountName,
                            ClientId = clientId,
                            ClientSecret = clientSecret,
                            RefreshToken = refreshToken
                        };
                        config.UserAccounts.Add(userEntry);
                    }
                    else if (string.IsNullOrWhiteSpace(userEntry.RefreshToken))
                    {
                        userEntry.ClientId = clientId;
                        userEntry.ClientSecret = clientSecret;
                        userEntry.RefreshToken = refreshToken;
                        userEntry.AccountName = accountName;
                    }

                    Plugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("Linked YouTube OAuth account '{Account}' to Jellyfin user '{User}'", accountName, targetUser);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bootstrap primary YouTube OAuth seed");
        }
    }

    private void TryInjectIndexHtmlScript()
    {
        try
        {
            var webPath = _appPaths.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                return;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                return;
            }

            const string scriptTag = "<script src=\"/api/youtubePlugin/Script\" defer></script>";
            var html = File.ReadAllText(indexFile);
            if (!html.Contains("/api/youtubePlugin/Script", StringComparison.OrdinalIgnoreCase))
            {
                var updated = html.Replace("</body>", $"{scriptTag}</body>", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(indexFile, updated);
                _logger.LogInformation("Injected YouTube SmartTube client script into Jellyfin index.html");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inject script into index.html");
        }
    }

    private async Task EnsureYouTubeLibraryAsync()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableYouTubeLibrary || string.IsNullOrWhiteSpace(config.StrmLibraryPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(config.StrmLibraryPath);
            EnsureLibraryArtwork(config.StrmLibraryPath, isMusic: false);

            var vfs = _libraryManager.GetVirtualFolders();
            var existingVf = vfs.FirstOrDefault(v => string.Equals(v.Name, "YouTube", StringComparison.OrdinalIgnoreCase));
            var needsRecreate = existingVf == null ||
                                existingVf.LibraryOptions == null ||
                                existingVf.LibraryOptions.EnableAutomaticSeriesGrouping ||
                                (existingVf.Locations != null && existingVf.Locations.Length > 1);

            if (existingVf != null && needsRecreate)
            {
                await _libraryManager.RemoveVirtualFolder("YouTube", false).ConfigureAwait(false);
                existingVf = null;
            }

            if (existingVf == null)
            {
#pragma warning disable CS0618
                var libraryOptions = new LibraryOptions
                {
                    EnableInternetProviders = true,
                    EnableRealtimeMonitor = true,
                    SaveLocalMetadata = false,
                    PathInfos = new[] { new MediaPathInfo { Path = config.StrmLibraryPath } },
                    TypeOptions = new[] { CreateYouTubeTypeOptions("Series"), CreateYouTubeTypeOptions("Season"), CreateYouTubeTypeOptions("Episode") }
                };
#pragma warning restore CS0618

                await _libraryManager.AddVirtualFolder("YouTube", CollectionTypeOptions.tvshows, libraryOptions, true).ConfigureAwait(false);
                _logger.LogInformation("Created native YouTube media library pointing to {Path}", config.StrmLibraryPath);
                _libraryManager.QueueLibraryScan();
            }

            EnsureLibraryArtwork("/config/data/root/default/YouTube", isMusic: false);

            if (config.EnableStrmSync)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
                        var added = await ScheduledTasks.SyncYouTubeStrmTask.PerformSyncAsync(client, config, _logger, CancellationToken.None).ConfigureAwait(false);
                        if (added > 0)
                        {
                            _libraryManager.QueueLibraryScan();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Initial YouTube .strm sync encountered an error");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to automatically create YouTube media library");
        }
    }

    private async Task EnsureYouTubeMusicLibraryAsync()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableYouTubeMusic || !config.EnableYouTubeMusicLibrary || string.IsNullOrWhiteSpace(config.YouTubeMusicLibraryPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(config.YouTubeMusicLibraryPath);
            EnsureLibraryArtwork(config.YouTubeMusicLibraryPath, isMusic: true);

            var vfs = _libraryManager.GetVirtualFolders();
            var existingVf = vfs.FirstOrDefault(v => string.Equals(v.Name, "YouTube Music", StringComparison.OrdinalIgnoreCase));

            if (existingVf == null)
            {
#pragma warning disable CS0618
                var libraryOptions = new LibraryOptions
                {
                    EnableInternetProviders = true,
                    EnableRealtimeMonitor = true,
                    SaveLocalMetadata = false,
                    PathInfos = new[] { new MediaPathInfo { Path = config.YouTubeMusicLibraryPath } },
                    TypeOptions = new[] { CreateYouTubeTypeOptions("MusicArtist"), CreateYouTubeTypeOptions("MusicAlbum"), CreateYouTubeTypeOptions("Audio") }
                };
#pragma warning restore CS0618

                await _libraryManager.AddVirtualFolder("YouTube Music", CollectionTypeOptions.music, libraryOptions, true).ConfigureAwait(false);
                _logger.LogInformation("Created native YouTube Music media library pointing to {Path}", config.YouTubeMusicLibraryPath);
                _libraryManager.QueueLibraryScan();
            }

            EnsureLibraryArtwork("/config/data/root/default/YouTube Music", isMusic: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to automatically create YouTube Music media library");
        }
    }

    private static TypeOptions CreateYouTubeTypeOptions(string type) =>
        new()
        {
            Type = type,
            MetadataFetchers = new[] { "YouTube" },
            MetadataFetcherOrder = new[] { "YouTube" },
            ImageFetchers = new[] { "YouTube" },
            ImageFetcherOrder = new[] { "YouTube" }
        };

    private static void EnsureLibraryArtwork(string targetDir, bool isMusic)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                return;
            }

            Directory.CreateDirectory(targetDir);
            var assembly = typeof(YouTubeStartupService).Assembly;
            var prefix = isMusic ? "music_" : "youtube_";

            var imagePairs = new (string ResName, string FileName)[]
            {
                ($"{prefix}poster.png", "poster.png"),
                ($"{prefix}poster.png", "folder.png"),
                ($"{prefix}backdrop.png", "backdrop.png"),
                ($"{prefix}backdrop.png", "fanart.png"),
                ($"{prefix}banner.png", "banner.png"),
                ($"{prefix}logo.png", "logo.png")
            };

            foreach (var (resName, fileName) in imagePairs)
            {
                var dst = Path.Combine(targetDir, fileName);
                var fullRes = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(resName, StringComparison.OrdinalIgnoreCase));
                if (fullRes == null)
                {
                    continue;
                }

                using var stream = assembly.GetManifestResourceStream(fullRes);
                if (stream != null)
                {
                    if (!File.Exists(dst) || new FileInfo(dst).Length != stream.Length)
                    {
                        using var fs = File.Create(dst);
                        stream.CopyTo(fs);
                    }
                }
            }
        }
        catch
        {
            // Ignore artwork copy errors
        }
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.EnableSponsorBlock || e.Item == null || e.Session == null || !e.PlaybackPositionTicks.HasValue)
            {
                return;
            }

            var videoId = YouTubeIdKeys.GetVideoId(e.Item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(e.Item.Path);
            if (string.IsNullOrWhiteSpace(videoId) && e.Item.Path != null && e.Item.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = e.Item.Path.Split("/api/youtubePlugin/stream/", StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    videoId = parts[1].Split('/')[0];
                }
            }

            if (string.IsNullOrWhiteSpace(videoId))
            {
                return;
            }

            using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
            var segments = await client.GetSponsorBlockSegmentsAsync(videoId, CancellationToken.None).ConfigureAwait(false);
            if (segments.Count == 0)
            {
                return;
            }

            var currentSec = TimeSpan.FromTicks(e.PlaybackPositionTicks.Value).TotalSeconds;
            foreach (var seg in segments)
            {
                if (seg.EndSeconds > seg.StartSeconds + 1.0 &&
                    currentSec >= seg.StartSeconds &&
                    currentSec < seg.EndSeconds - 1.2)
                {
                    var skipKey = $"{e.Session.Id}_{videoId}_{seg.Uuid}";
                    if (_recentSkips.TryGetValue(skipKey, out var lastSkip) && (DateTime.UtcNow - lastSkip).TotalSeconds < 12)
                    {
                        return;
                    }

                    _recentSkips[skipKey] = DateTime.UtcNow;
                    var targetTicks = TimeSpan.FromSeconds(seg.EndSeconds).Ticks;

                    await _sessionManager.SendPlaystateCommand(
                        null,
                        e.Session.Id,
                        new PlaystateRequest
                        {
                            Command = PlaystateCommand.Seek,
                            SeekPositionTicks = targetTicks
                        },
                        CancellationToken.None).ConfigureAwait(false);

                    _logger.LogInformation("SponsorBlock auto-skipped segment [{Category}] ({Start:0.0}s -> {End:0.0}s) for video {VideoId} on session {Session}",
                        seg.Category, seg.StartSeconds, seg.EndSeconds, videoId, e.Session.DeviceName);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error in SponsorBlock playback progress handler");
        }
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.EnableFavoriteToLikeSync || e.Item == null || e.UserData == null)
            {
                return;
            }

            if (e.SaveReason != UserDataSaveReason.UpdateUserRating)
            {
                return;
            }

            var videoId = YouTubeIdKeys.GetVideoId(e.Item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(e.Item.Path);
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return;
            }

            var userKey = e.UserId.Equals(Guid.Empty) ? (config.PrimaryJellyfinUser ?? string.Empty) : e.UserId.ToString("N");
            var rating = e.UserData.IsFavorite ? "like" : "none";

            using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
            await client.RateVideoAsync(videoId, rating, userKey, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Synced Jellyfin Favorite={IsFav} -> YouTube Rating='{Rating}' for video {VideoId}", e.UserData.IsFavorite, rating, videoId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error syncing Jellyfin favorite to YouTube like");
        }
    }
}

/// <summary>
/// Intercepts Jellyfin's PlaybackInfo and legacy stream requests, redirecting to direct YouTube CDN streams.
/// </summary>
public class YouTubePlaybackFilter : Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter, Microsoft.AspNetCore.Mvc.Filters.IAsyncResultFilter
{
    private readonly ILibraryManager _libraryManager;

    public YouTubePlaybackFilter(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public async Task OnActionExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var isVideoPath = path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase);
        var isAudioPath = path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase);

        if ((isVideoPath || isAudioPath) &&
            (path.Contains("/stream", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/universal", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/main.m3u8", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/hls", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && Guid.TryParse(segments[1], out var itemId))
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item != null)
                {
                    var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
                    if (!string.IsNullOrWhiteSpace(videoId))
                    {
                        var req = context.HttpContext.Request;
                        var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault();
                        var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
                        var scheme = !string.IsNullOrWhiteSpace(proto) ? proto : req.Scheme;
                        var serverUrl = Plugin.Instance?.Configuration.GetPublicServerUrl(scheme, host)
                            ?? (!string.IsNullOrWhiteSpace(host) ? $"{scheme}://{host}" : string.Empty);

                        var isAudio = isAudioPath || item is MediaBrowser.Controller.Entities.Audio.Audio ||
                                      (item.Path != null && (item.Path.Contains("YouTubeAudio", StringComparison.OrdinalIgnoreCase) ||
                                                             item.Path.Contains("/audio.m4a", StringComparison.OrdinalIgnoreCase)));
                        var target = isAudio
                            ? $"{serverUrl}/api/youtubePlugin/stream/{videoId}/audio.m4a"
                            : $"{serverUrl}/api/youtubePlugin/stream/{videoId}/master.m3u8";

                        context.Result = new Microsoft.AspNetCore.Mvc.RedirectResult(target);
                        return;
                    }
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    public async Task OnResultExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.ResultExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) &&
            context.Result is Microsoft.AspNetCore.Mvc.ObjectResult { Value: MediaBrowser.Model.MediaInfo.PlaybackInfoResponse pbResp } &&
            pbResp.MediaSources is { Count: > 0 })
        {
            var ytSources = pbResp.MediaSources
                .Where(m => m.Path != null && m.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ytSources.Count > 0)
            {
                var req = context.HttpContext.Request;
                var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault();
                var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
                var scheme = !string.IsNullOrWhiteSpace(proto) ? proto : req.Scheme;
                var serverUrl = Plugin.Instance?.Configuration.GetPublicServerUrl(scheme, host)
                    ?? (!string.IsNullOrWhiteSpace(host) ? $"{scheme}://{host}" : string.Empty);

                foreach (var ytSource in ytSources)
                {
                    if (!string.IsNullOrWhiteSpace(serverUrl))
                    {
                        ytSource.Path = RewritePluginUrlHost(ytSource.Path, serverUrl);
                        if (ytSource.MediaStreams != null)
                        {
                            foreach (var ms in ytSource.MediaStreams)
                            {
                                if (!string.IsNullOrWhiteSpace(ms.Path) && ms.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                                {
                                    ms.Path = RewritePluginUrlHost(ms.Path, serverUrl);
                                }

                                if (!string.IsNullOrWhiteSpace(ms.DeliveryUrl) && ms.DeliveryUrl.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                                {
                                    ms.DeliveryUrl = RewritePluginUrlHost(ms.DeliveryUrl, serverUrl);
                                }
                            }
                        }
                    }

                    ytSource.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
                    ytSource.IsRemote = true;
                    ytSource.SupportsDirectPlay = true;
                    ytSource.SupportsDirectStream = true;
                    ytSource.SupportsTranscoding = true;
                }

                pbResp.MediaSources = ytSources;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static string RewritePluginUrlHost(string url, string serverUrl)
    {
        var idx = url.IndexOf("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? $"{serverUrl.TrimEnd('/')}{url[idx..]}" : url;
    }
}
