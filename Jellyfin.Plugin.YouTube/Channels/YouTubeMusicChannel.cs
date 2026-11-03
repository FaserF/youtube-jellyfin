using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Channels;

/// <summary>
/// Native YouTube Music Jellyfin Channel.
/// Supports per-Jellyfin-user linked YouTube accounts (with automatic link for 'fabian')
/// and optional public/anonymous mode for unlinked users.
/// </summary>
public class YouTubeMusicChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeMusicChannel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeMusicChannel(
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeMusicChannel> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube Music";
    public string Description => "YouTube Music for Jellyfin: Personal Supermix, Liked Songs, Listen Again, Music Playlists, Charts & Ad-Free HQ Audio Streaming.";
    public string DataVersion => "1.0.0";
    public string HomePageUrl => "https://music.youtube.com";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType>
            {
                ChannelMediaContentType.Song,
                ChannelMediaContentType.Clip
            },
            MediaTypes = new List<ChannelMediaType>
            {
                ChannelMediaType.Audio,
                ChannelMediaType.Video
            },
            SupportsContentDownloading = true,
            SupportsSortOrderToggle = true,
            MaxPageSize = 50
        };
    }

    public bool IsEnabledFor(string userId)
    {
        var config = Config;
        if (!config.EnableYouTubeMusic || !config.EnableYouTubeMusicChannel)
        {
            return false;
        }

        if (config.EnablePublicFallbackForUnlinkedUsers)
        {
            return true;
        }

        return config.GetAccountForUser(userId) != null;
    }

    public string GetCacheKey(string? userId)
    {
        var hasAuth = Config.GetAccountForUser(userId) != null;
        return $"ytm_{DataVersion}_{PluginConfiguration.NormalizeId(userId)}_{hasAuth}_{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        var config = Config;
        if (!config.EnableYouTubeMusic || !config.EnableYouTubeMusicChannel)
        {
            return new ChannelItemResult { Items = items };
        }

        var rawUserId = query.UserId.Equals(Guid.Empty) ? null : query.UserId.ToString("N");
        var account = config.GetAccountForUser(rawUserId);
        var isAuthenticated = account != null && !string.IsNullOrWhiteSpace(account.RefreshToken);
        var effectiveUserKey = isAuthenticated ? (account!.JellyfinUser ?? rawUserId) : null;
        var limit = config.YouTubeMusicMaxItemsPerFeed > 0 ? config.YouTubeMusicMaxItemsPerFeed : 25;

        if (string.IsNullOrWhiteSpace(query.FolderId))
        {
            if (isAuthenticated)
            {
                var personalCategories = new (string Id, string Title, string Overview)[]
                {
                    ("ytm_supermix", "🎧 My Supermix & Home", $"Personal YouTube Music mix & recommendations for {account!.AccountName}"),
                    ("ytm_liked", "❤️ Liked Songs (Your Likes)", "Songs and music tracks you have liked"),
                    ("ytm_history", "🔁 Listen Again & History", "Recently played songs and music tracks"),
                    ("ytm_playlists", "💿 My Music Playlists", "Your personal and saved playlists"),
                    ("ytm_charts", "🔥 Top Music Charts (DE & Global)", "Trending music charts and top hits"),
                    ("ytm_new", "✨ New Releases & Discover", "Fresh music releases and trending singles"),
                    ("ytm_chill", "☕ Chill, Lo-Fi & Focus", "Relaxing background music and study beats"),
                    ("ytm_workout", "⚡ Workout & Energy", "High-energy electronic, rock & pop tracks")
                };

                foreach (var (id, title, desc) in personalCategories)
                {
                    items.Add(new ChannelItemInfo
                    {
                        Id = id,
                        Name = title,
                        Overview = desc,
                        Type = ChannelItemType.Folder
                    });
                }
            }
            else
            {
                var publicCategories = new (string Id, string Title, string Overview)[]
                {
                    ("ytm_charts", "🔥 Top Music Charts (Public)", "Currently trending music charts (Unlinked Public Mode)"),
                    ("ytm_new", "✨ New Releases & Hits (Public)", "Popular new music releases"),
                    ("ytm_chill", "☕ Chill, Lo-Fi & Focus (Public)", "Relaxing background music and study beats"),
                    ("ytm_workout", "⚡ Workout & Energy (Public)", "High-energy electronic, rock & pop tracks"),
                    ("ytm_login_info", "🔑 Link Personal YouTube Music Account", "Link your personal YouTube / YouTube Music account via TV Device Code")
                };

                foreach (var (id, title, desc) in publicCategories)
                {
                    items.Add(new ChannelItemInfo
                    {
                        Id = id,
                        Name = title,
                        Overview = desc,
                        Type = ChannelItemType.Folder
                    });
                }
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        using var client = CreateClient();

        if (query.FolderId == "ytm_login_info")
        {
            var codeResp = await client.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
            if (codeResp != null && !string.IsNullOrWhiteSpace(codeResp.UserCode))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = "ytm_login_code_display",
                    Name = $"🔑 TV Code: {codeResp.UserCode} -> Go to google.com/device",
                    Overview = $"To link your personal YouTube Music account for Jellyfin user '{rawUserId ?? "current"}':\n1. Open https://www.google.com/device\n2. Enter code: {codeResp.UserCode}\n3. Complete pairing in Jellyfin Dashboard -> Plugins -> YouTube.",
                    Type = ChannelItemType.Folder
                });
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        if (query.FolderId == "ytm_playlists")
        {
            var playlists = await client.GetUserPlaylistsAsync(effectiveUserKey, 50, cancellationToken).ConfigureAwait(false);
            foreach (var pl in playlists)
            {
                var folder = new ChannelItemInfo
                {
                    Id = $"ytm_pl_{pl.PlaylistId}",
                    Name = $"💿 {pl.Title} ({pl.ItemCount})",
                    Overview = pl.Description,
                    Type = ChannelItemType.Folder,
                    ImageUrl = pl.ThumbnailUrl
                };
                YouTubeIdKeys.SetPlaylistId(folder, pl.PlaylistId);
                items.Add(folder);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        if (query.FolderId.StartsWith("ytm_pl_", StringComparison.Ordinal))
        {
            var playlistId = query.FolderId["ytm_pl_".Length..];
            var tracks = await client.GetPlaylistVideosAsync(playlistId, effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
            var mapped = MapMusicTracks(tracks);
            return new ChannelItemResult { Items = mapped, TotalRecordCount = mapped.Count };
        }

        List<YouTubeVideoItem> musicTracks;
        switch (query.FolderId)
        {
            case "ytm_supermix":
                musicTracks = await client.GetBrowseFeedAsync("FEmusic", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                if (musicTracks.Count == 0)
                {
                    musicTracks = await client.SearchVideosAsync("Official Music Video Audio Mix", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                }

                break;
            case "ytm_liked":
                musicTracks = await client.GetBrowseFeedAsync("VLLV", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
            case "ytm_history":
                musicTracks = await client.GetBrowseFeedAsync("FEhistory", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
            case "ytm_charts":
                musicTracks = await client.GetBrowseFeedAsync("FEmusic", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                if (musicTracks.Count == 0)
                {
                    musicTracks = await client.SearchVideosAsync("Top Music Charts Official Audio 2026", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                }

                break;
            case "ytm_new":
                musicTracks = await client.SearchVideosAsync("New Music Releases Official Audio", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
            case "ytm_chill":
                musicTracks = await client.SearchVideosAsync("Chill Lo-Fi Beats Study Relax Music", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
            case "ytm_workout":
                musicTracks = await client.SearchVideosAsync("Workout Gym Electronic Music Mix", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
            default:
                musicTracks = await client.GetBrowseFeedAsync("FEmusic", effectiveUserKey, limit, cancellationToken).ConfigureAwait(false);
                break;
        }

        var resultItems = MapMusicTracks(musicTracks);
        return new ChannelItemResult { Items = resultItems, TotalRecordCount = resultItems.Count };
    }

    private static List<ChannelItemInfo> MapMusicTracks(List<YouTubeVideoItem> tracks)
    {
        var list = new List<ChannelItemInfo>();
        var idx = 1;
        foreach (var t in tracks)
        {
            var artist = !string.IsNullOrWhiteSpace(t.ChannelTitle) ? t.ChannelTitle.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase) : "YouTube Music";
            var displayTitle = !string.IsNullOrWhiteSpace(artist) && !t.Title.Contains(artist, StringComparison.OrdinalIgnoreCase)
                ? $"{artist} — {t.Title}"
                : t.Title;

            var item = new ChannelItemInfo
            {
                Id = $"ytm_{t.VideoId}",
                Name = displayTitle,
                SeriesName = artist,
                Artists = new List<string> { artist },
                Album = "YouTube Music",
                Overview = $"🎵 {artist} — {t.Title}\n🔗 https://music.youtube.com/watch?v={t.VideoId}",
                Type = ChannelItemType.Media,
                ContentType = ChannelMediaContentType.Song,
                MediaType = ChannelMediaType.Audio,
                IndexNumber = idx++,
                ParentIndexNumber = 1,
                PremiereDate = t.PublishedAt ?? DateTime.UtcNow.Date,
                ProductionYear = (t.PublishedAt ?? DateTime.UtcNow).Year,
                CommunityRating = 8.8f,
                RunTimeTicks = t.DurationSeconds > 0 ? TimeSpan.FromSeconds(t.DurationSeconds).Ticks : null,
                ImageUrl = !string.IsNullOrWhiteSpace(t.ThumbnailUrl) ? t.ThumbnailUrl : $"https://i.ytimg.com/vi/{t.VideoId}/hqdefault.jpg"
            };

            YouTubeIdKeys.SetAudioId(item, t.VideoId);
            list.Add(item);
        }

        return list;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var videoId = id.StartsWith("ytm_", StringComparison.Ordinal) ? id["ytm_".Length..] : id;
        using var client = CreateClient();
        var streamInfo = await client.ResolveStreamAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (streamInfo == null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var config = Config;
        var bitrate = (config.YouTubeMusicPreferredBitrate > 0 ? config.YouTubeMusicPreferredBitrate : 256) * 1000;

        var source = new MediaSourceInfo
        {
            Id = videoId,
            Name = $"{streamInfo.Title} (YouTube Music HQ Audio)",
            Path = $"http://127.0.0.1:8096/api/youtubePlugin/stream/{videoId}/audio.m4a",
            Protocol = MediaProtocol.Http,
            Container = "m4a",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RunTimeTicks = streamInfo.DurationSeconds > 0 ? TimeSpan.FromSeconds(streamInfo.DurationSeconds).Ticks : null,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Audio,
                    Index = 0,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 44100,
                    BitRate = bitrate,
                    Language = "und",
                    Title = "YouTube Music Audio (AAC)",
                    IsDefault = true
                }
            }
        };

        return new[] { source };
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken) =>
        Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };
}
