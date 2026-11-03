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
/// Startup &amp; background service that:
/// 1. Auto-imports existing Google/YouTube OAuth credentials for 'fabian' if not yet configured.
/// 2. Injects &lt;script src="/api/youtubePlugin/Script"&gt;&lt;/script&gt; into Jellyfin's index.html.
/// 3. Hooks ISessionManager.PlaybackProgress for automatic server-side SponsorBlock skipping on Android TV &amp; all clients!
/// 4. Hooks IUserDataManager.UserDataSaved so marking a YouTube video as Favorite (♥️) in Jellyfin automatically Likes it on YouTube!
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
        TryBootstrapFabianAccount();
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

    private void TryBootstrapFabianAccount()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            const string seedFile = "/config/data/plugins/YouTube/fabian_oauth_seed.json";
            if (File.Exists(seedFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(seedFile));
                var root = doc.RootElement;
                var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty;
                var clientSecret = root.TryGetProperty("clientSecret", out var cs) ? cs.GetString() ?? string.Empty : string.Empty;
                var refreshToken = root.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? string.Empty : string.Empty;
                var accountName = root.TryGetProperty("accountName", out var an) ? an.GetString() ?? "Fabian (YouTube)" : "Fabian (YouTube)";

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
                    var fabEntry = config.UserAccounts.FirstOrDefault(u =>
                        string.Equals(u.JellyfinUser, "fabian", StringComparison.OrdinalIgnoreCase));
                    if (fabEntry == null)
                    {
                        fabEntry = new YouTubeUserAccountEntry
                        {
                            JellyfinUser = "fabian",
                            JellyfinUserId = "c24de2a9fe3644569746e53b80490646",
                            AccountName = accountName,
                            ClientId = clientId,
                            ClientSecret = clientSecret,
                            RefreshToken = refreshToken
                        };
                        config.UserAccounts.Add(fabEntry);
                    }
                    else if (string.IsNullOrWhiteSpace(fabEntry.RefreshToken))
                    {
                        fabEntry.ClientId = clientId;
                        fabEntry.ClientSecret = clientSecret;
                        fabEntry.RefreshToken = refreshToken;
                        fabEntry.AccountName = accountName;
                    }

                    Plugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("Linked YouTube OAuth account '{Account}' to Jellyfin user 'fabian'", accountName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bootstrap Fabian YouTube OAuth seed");
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
            EnsureLibraryArtwork(config.StrmLibraryPath);

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
                    EnablePhotos = false,
                    EnableTrickplayImageExtraction = false,
                    ExtractChapterImagesDuringLibraryScan = false,
                    EnableAutomaticSeriesGrouping = false,
                    PathInfos = new[] { new MediaPathInfo { Path = config.StrmLibraryPath } },
                    TypeOptions = new[]
                    {
                        new TypeOptions
                        {
                            Type = "Series",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        },
                        new TypeOptions
                        {
                            Type = "Season",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        },
                        new TypeOptions
                        {
                            Type = "Episode",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        }
                    }
                };
#pragma warning restore CS0618

                await _libraryManager.AddVirtualFolder("YouTube", CollectionTypeOptions.tvshows, libraryOptions, true).ConfigureAwait(false);
                _logger.LogInformation("Created native YouTube media library pointing to {Path}", config.StrmLibraryPath);
                _libraryManager.QueueLibraryScan();
            }

            EnsureLibraryArtwork("/config/data/root/default/YouTube");

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
            EnsureLibraryArtwork(config.YouTubeMusicLibraryPath);

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
                    EnablePhotos = false,
                    EnableTrickplayImageExtraction = false,
                    ExtractChapterImagesDuringLibraryScan = false,
                    PathInfos = new[] { new MediaPathInfo { Path = config.YouTubeMusicLibraryPath } },
                    TypeOptions = new[]
                    {
                        new TypeOptions
                        {
                            Type = "MusicArtist",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        },
                        new TypeOptions
                        {
                            Type = "MusicAlbum",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        },
                        new TypeOptions
                        {
                            Type = "Audio",
                            MetadataFetchers = new[] { "YouTube" },
                            MetadataFetcherOrder = new[] { "YouTube" },
                            ImageFetchers = new[] { "YouTube" },
                            ImageFetcherOrder = new[] { "YouTube" }
                        }
                    }
                };
#pragma warning restore CS0618

                await _libraryManager.AddVirtualFolder("YouTube Music", CollectionTypeOptions.music, libraryOptions, true).ConfigureAwait(false);
                _logger.LogInformation("Created native YouTube Music media library pointing to {Path}", config.YouTubeMusicLibraryPath);
                _libraryManager.QueueLibraryScan();
            }

            EnsureLibraryArtwork("/config/data/root/default/YouTube Music");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to automatically create YouTube Music media library");
        }
    }

    private static void EnsureLibraryArtwork(string targetDir)
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(YouTubeStartupService).Assembly.Location);
            if (string.IsNullOrWhiteSpace(asmDir))
            {
                return;
            }

            foreach (var name in new[] { "poster.png", "folder.png", "backdrop.png", "banner.png", "logo.png" })
            {
                var src = Path.Combine(asmDir, name);
                var dst = Path.Combine(targetDir, name);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst, overwrite: true);
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

            var userKey = e.UserId.Equals(Guid.Empty) ? "fabian" : e.UserId.ToString("N");
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
