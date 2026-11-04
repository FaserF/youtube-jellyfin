using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Services;

/// <summary>
/// Hosted service that synchronizes YouTube Watch Later, Liked Videos, and custom YouTube Playlists
/// directly into native Jellyfin Playlists and Jellyfin BoxSet Collections so all native Jellyfin tabs
/// (Playlists, Collections, Suggestions, Next Up, Continue Watching) are populated automatically.
/// </summary>
public class YouTubePlaylistAndCollectionSyncService : IHostedService
{
    private readonly IPlaylistManager _playlistManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubePlaylistAndCollectionSyncService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public YouTubePlaylistAndCollectionSyncService(
        IPlaylistManager playlistManager,
        ICollectionManager collectionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubePlaylistAndCollectionSyncService> logger,
        ILoggerFactory loggerFactory)
    {
        _playlistManager = playlistManager;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
                await SyncAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Initial YouTube playlist and collection sync encountered a transient error");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var primaryUser = config.PrimaryJellyfinUser ?? string.Empty;

            using var client = new YouTubeApiClient(
                _httpClientFactory.CreateClient("YouTube"),
                _loggerFactory.CreateLogger<YouTubeApiClient>());

            var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
                Recursive = true
            });

            var videoMap = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var ep in allEpisodes)
            {
                var vid = YouTubeIdKeys.GetVideoId(ep.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(ep.Path);
                if (!string.IsNullOrWhiteSpace(vid) && !videoMap.ContainsKey(vid))
                {
                    videoMap[vid] = ep;
                }
            }

            if (videoMap.Count == 0)
            {
                return;
            }

            Guid.TryParse(config.PrimaryJellyfinUserId, out var userGuid);
            if (userGuid == Guid.Empty)
            {
                var userObj = _userManager.GetUserByName(primaryUser);
                if (userObj != null)
                {
                    userGuid = userObj.Id;
                }
            }

            // Clean up any legacy Jellyfin Playlists previously created by the plugin so the separate 'Playlists' tab is removed
            try
            {
                var legacyPlaylists = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Playlist },
                    Recursive = true
                }).OfType<Playlist>().Where(p => p.Name != null && p.Name.Contains("YouTube", StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var lp in legacyPlaylists)
                {
                    _libraryManager.DeleteItem(lp, new DeleteOptions { DeleteFileLocation = true }, true);
                    _logger.LogInformation("Purged legacy YouTube playlist '{Name}' in favor of Collections", lp.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not purge legacy YouTube playlists");
            }

            // 1. Sync Native Jellyfin Collections in 'Sammlungen' (Watch Later, Liked Videos, Subscriptions)
            var collectionFeeds = new (string Name, string BrowseId)[]
            {
                ("YouTube: Watch Later", "VLWL"),
                ("YouTube: Liked Videos", "VLLV"),
                ("YouTube: Recommendations", "FEwhat_to_watch"),
                ("YouTube: Subscriptions", "FEsubscriptions")
            };

            foreach (var (colName, browseId) in collectionFeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feedVideos = await client.GetBrowseFeedAsync(browseId, primaryUser, 30, cancellationToken).ConfigureAwait(false);
                var itemIds = feedVideos
                    .Where(v => !string.IsNullOrWhiteSpace(v.VideoId) && videoMap.ContainsKey(v.VideoId))
                    .Select(v => videoMap[v.VideoId].Id)
                    .Distinct()
                    .ToList();

                if (itemIds.Count > 0)
                {
                    await UpsertCollectionOnlyAsync(colName, itemIds).ConfigureAwait(false);
                }
            }

            // 2. Sync Custom User Playlists from YouTube into BoxSet Collections
            var customPlaylists = await client.GetUserPlaylistsAsync(primaryUser, 20, cancellationToken).ConfigureAwait(false);
            foreach (var pl in customPlaylists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plVideos = await client.GetPlaylistVideosAsync(pl.PlaylistId, primaryUser, 30, cancellationToken).ConfigureAwait(false);
                var itemIds = plVideos
                    .Where(v => !string.IsNullOrWhiteSpace(v.VideoId) && videoMap.ContainsKey(v.VideoId))
                    .Select(v => videoMap[v.VideoId].Id)
                    .Distinct()
                    .ToList();

                if (itemIds.Count > 0)
                {
                    await UpsertCollectionOnlyAsync($"YouTube: {pl.Title}", itemIds).ConfigureAwait(false);
                }
            }

            // 3. Sync Subscribed Channels into 'YouTube: Subscribed Channels' BoxSet Collection
            var allSeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            });
            var ytSeriesIds = allSeries
                .Where(s => s.Path != null && s.Path.Contains("[YouTubeChannel-", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Id)
                .Distinct()
                .ToList();

            if (ytSeriesIds.Count > 0)
            {
                await UpsertCollectionOnlyAsync("YouTube: Subscribed Channels", ytSeriesIds).ConfigureAwait(false);
            }

            // 4. Sync YouTube Music Tracks into Collections
            if (config.EnableYouTubeMusic && config.EnableYouTubeMusicPlaylistsSync)
            {
                var allAudio = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    Recursive = true
                });

                var audioMap = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in allAudio)
                {
                    var vid = YouTubeIdKeys.GetVideoId(a.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(a.Path);
                    if (!string.IsNullOrWhiteSpace(vid) && !audioMap.ContainsKey(vid))
                    {
                        audioMap[vid] = a;
                    }
                }

                if (audioMap.Count > 0)
                {
                    var musicFeeds = new (string Name, string BrowseId)[]
                    {
                        ("YouTube Music: Supermix & Charts", "FEmusic"),
                        ("YouTube Music: Liked Songs", "VLLV"),
                        ("YouTube Music: Listen Again", "FEhistory")
                    };

                    foreach (var (colName, browseId) in musicFeeds)
                    {
                        var tracks = await client.GetBrowseFeedAsync(browseId, primaryUser, 30, cancellationToken).ConfigureAwait(false);
                        var trackIds = tracks
                            .Where(v => !string.IsNullOrWhiteSpace(v.VideoId) && audioMap.ContainsKey(v.VideoId))
                            .Select(v => audioMap[v.VideoId].Id)
                            .Distinct()
                            .ToList();

                        if (trackIds.Count > 0)
                        {
                            await UpsertCollectionOnlyAsync(colName, trackIds).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in YouTubePlaylistAndCollectionSyncService");
        }
        finally
        {
            _syncLock.Release();
        }
    }



    private async Task UpsertCollectionOnlyAsync(string name, List<Guid> itemIds)
    {
        try
        {
            var existingBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                CollapseBoxSetItems = false
            }).OfType<BoxSet>().ToList();

            var boxSet = existingBoxSets.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            if (boxSet == null)
            {
                boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                {
                    Name = name,
                    IsLocked = false
                }).ConfigureAwait(false);
            }

            var currentIds = boxSet.Children.Select(c => c.Id).ToHashSet();
            var toAdd = itemIds.Where(id => !currentIds.Contains(id)).ToList();
            if (toAdd.Count > 0)
            {
                await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not create/update Jellyfin collection '{Name}'", name);
        }
    }
}
