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
/// SmartTube 1:1 Parity Native Jellyfin Channel.
/// Supports per-Jellyfin-user linked YouTube accounts (with automatic link for 'fabian')
/// and public/anonymous mode for unlinked users.
/// </summary>
public class YouTubeChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeChannel> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeChannel(
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeChannel> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube";
    public string Description => "SmartTube 1:1 Parity for Jellyfin: Personal Home Feed, Subscriptions, Continue Watching, Watch Later, Liked Videos, Channels, SponsorBlock & 4K Ad-Free Streaming.";
    public string DataVersion => "1.0.0";
    public string HomePageUrl => "https://www.youtube.com";
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
                ChannelMediaContentType.Episode,
                ChannelMediaContentType.Movie,
                ChannelMediaContentType.Clip,
                ChannelMediaContentType.Song
            },
            MediaTypes = new List<ChannelMediaType>
            {
                ChannelMediaType.Video,
                ChannelMediaType.Audio
            },
            SupportsContentDownloading = true,
            SupportsSortOrderToggle = true,
            MaxPageSize = 50
        };
    }

    public bool IsEnabledFor(string userId)
    {
        var config = Config;
        if (!config.EnableStreamingChannel)
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
        return $"{DataVersion}_{PluginConfiguration.NormalizeId(userId)}_{hasAuth}_{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();
        var config = Config;
        if (!config.EnableStreamingChannel)
        {
            return new ChannelItemResult { Items = items };
        }

        var rawUserId = query.UserId.Equals(Guid.Empty) ? null : query.UserId.ToString("N");
        var account = config.GetAccountForUser(rawUserId);
        var isAuthenticated = account != null && !string.IsNullOrWhiteSpace(account.RefreshToken);
        var effectiveUserKey = isAuthenticated ? (account!.JellyfinUser ?? rawUserId) : null;

        // Root Navigation (SmartTube Sidebar Parity)
        if (string.IsNullOrWhiteSpace(query.FolderId))
        {
            if (isAuthenticated)
            {
                var personalCategories = new (string Id, string Title, string Overview)[]
                {
                    ("cat_home", "🏠 Home & Recommended", $"Personal YouTube recommendations for {account!.AccountName}"),
                    ("cat_subscriptions", "📺 Subscriptions Feed", "Latest uploads from your subscribed YouTube channels"),
                    ("cat_continue", "▶️ Continue Watching & History", "Resume partially watched videos and browse your YouTube history"),
                    ("cat_channels", "👤 Subscribed Channels (A–Z)", "Browse all your subscribed channels and their uploads"),
                    ("cat_watch_later", "⏱️ Watch Later", "Videos saved to your Watch Later playlist"),
                    ("cat_liked", "👍 Liked Videos", "Videos you have liked on YouTube"),
                    ("cat_playlists", "📁 My Playlists", "Your personal and saved YouTube playlists"),
                    ("cat_ytmusic", "🎵 YouTube Music (Audio Streaming)", "Personal Supermix, Liked Songs, Listen Again & Music Charts in HQ Audio"),
                    ("cat_trending", "🔥 Trending", "Currently trending videos in your region"),
                    ("cat_gaming", "🎮 Gaming", "Top gaming streams and videos"),
                    ("cat_music", "🎵 Music", "Music videos and charts"),
                    ("cat_live", "🔴 Live Streams", "Currently active live broadcasts")
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
                    ("cat_trending", "🔥 Trending (Public)", "Currently trending videos on YouTube (Unlinked Public Mode)"),
                    ("cat_home", "🏠 Popular & Discover (Public)", "Popular technology, news, and entertainment videos"),
                    ("cat_ytmusic", "🎵 YouTube Music (Public Audio)", "Top music charts, new releases, chill & workout audio tracks"),
                    ("cat_gaming", "🎮 Gaming (Public)", "Popular gaming videos"),
                    ("cat_music", "🎵 Music (Public)", "Popular music videos"),
                    ("cat_live", "🔴 Live Streams (Public)", "Public live broadcasts"),
                    ("cat_login_info", "🔑 Link Personal YouTube Account", "Open Jellyfin Dashboard -> Plugins -> YouTube to link your personal YouTube account via TV code")
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

        if (query.FolderId == "cat_login_info")
        {
            var codeResp = await client.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
            if (codeResp != null && !string.IsNullOrWhiteSpace(codeResp.UserCode))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = "login_code_display",
                    Name = $"🔑 TV Code: {codeResp.UserCode} -> Go to google.com/device",
                    Overview = $"To link your personal YouTube account for Jellyfin user '{rawUserId ?? "current"}':\n1. Open https://www.google.com/device (or youtube.com/activate)\n2. Enter code: {codeResp.UserCode}\n3. Complete pairing in Jellyfin Dashboard -> Plugins -> YouTube.",
                    Type = ChannelItemType.Folder
                });
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // Subscribed Channels Folder (A-Z)
        if (query.FolderId == "cat_channels")
        {
            var channels = await client.GetSubscribedChannelsAsync(effectiveUserKey, 50, cancellationToken).ConfigureAwait(false);
            foreach (var ch in channels)
            {
                var folder = new ChannelItemInfo
                {
                    Id = $"chan_{ch.ChannelId}",
                    Name = ch.Title,
                    Overview = ch.Description,
                    Type = ChannelItemType.Folder,
                    ImageUrl = ch.ThumbnailUrl
                };
                YouTubeIdKeys.SetChannelId(folder, ch.ChannelId);
                items.Add(folder);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // Inside a specific Channel -> list latest videos from RSS/Innertube
        if (query.FolderId.StartsWith("chan_", StringComparison.Ordinal))
        {
            var channelId = query.FolderId["chan_".Length..];
            var videos = await client.GetChannelRssFeedAsync(channelId, 25, cancellationToken).ConfigureAwait(false);
            if (videos.Count == 0)
            {
                videos = await client.GetBrowseFeedAsync(channelId, effectiveUserKey, 25, cancellationToken).ConfigureAwait(false);
            }

            var mapped = await EnrichAndMapVideosAsync(client, videos, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult { Items = mapped, TotalRecordCount = mapped.Count };
        }

        // User Playlists Folder
        if (query.FolderId == "cat_playlists")
        {
            var playlists = await client.GetUserPlaylistsAsync(effectiveUserKey, 50, cancellationToken).ConfigureAwait(false);
            foreach (var pl in playlists)
            {
                var folder = new ChannelItemInfo
                {
                    Id = $"pl_{pl.PlaylistId}",
                    Name = $"{pl.Title} ({pl.ItemCount})",
                    Overview = pl.Description,
                    Type = ChannelItemType.Folder,
                    ImageUrl = pl.ThumbnailUrl
                };
                YouTubeIdKeys.SetPlaylistId(folder, pl.PlaylistId);
                items.Add(folder);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // Inside a specific Playlist
        if (query.FolderId.StartsWith("pl_", StringComparison.Ordinal))
        {
            var playlistId = query.FolderId["pl_".Length..];
            var videos = await client.GetPlaylistVideosAsync(playlistId, effectiveUserKey, 40, cancellationToken).ConfigureAwait(false);
            var mapped = await EnrichAndMapVideosAsync(client, videos, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult { Items = mapped, TotalRecordCount = mapped.Count };
        }

        // YouTube Music Audio Submenu inside YouTube Channel
        if (query.FolderId == "cat_ytmusic")
        {
            var ytmFolders = new (string Id, string Title, string Desc)[]
            {
                ("ytm_sub_supermix", "🎧 My Supermix & Top Music Charts", "Top music charts and personal mix (Audio Stream)"),
                ("ytm_sub_liked", "❤️ Liked Songs & Music", "Songs and tracks you have liked (Audio Stream)"),
                ("ytm_sub_history", "🔁 Listen Again & History", "Recently played tracks (Audio Stream)"),
                ("ytm_sub_chill", "☕ Chill, Lo-Fi & Study Beats", "Relaxing background music (Audio Stream)"),
                ("ytm_sub_workout", "⚡ Workout & Electronic Mix", "High-energy workout music (Audio Stream)")
            };
            foreach (var (id, title, desc) in ytmFolders)
            {
                items.Add(new ChannelItemInfo { Id = id, Name = title, Overview = desc, Type = ChannelItemType.Folder });
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        if (query.FolderId.StartsWith("ytm_sub_", StringComparison.Ordinal))
        {
            var ytmLimit = config.YouTubeMusicMaxItemsPerFeed > 0 ? config.YouTubeMusicMaxItemsPerFeed : 25;
            var tracks = query.FolderId switch
            {
                "ytm_sub_liked" => await client.GetBrowseFeedAsync("VLLV", effectiveUserKey, ytmLimit, cancellationToken).ConfigureAwait(false),
                "ytm_sub_history" => await client.GetBrowseFeedAsync("FEhistory", effectiveUserKey, ytmLimit, cancellationToken).ConfigureAwait(false),
                "ytm_sub_chill" => await client.SearchVideosAsync("Chill Lo-Fi Study Music Beats", effectiveUserKey, ytmLimit, cancellationToken).ConfigureAwait(false),
                "ytm_sub_workout" => await client.SearchVideosAsync("Workout Electronic Music Mix", effectiveUserKey, ytmLimit, cancellationToken).ConfigureAwait(false),
                _ => await client.GetBrowseFeedAsync("FEmusic", effectiveUserKey, ytmLimit, cancellationToken).ConfigureAwait(false)
            };

            var idx = 1;
            foreach (var t in tracks)
            {
                var artist = !string.IsNullOrWhiteSpace(t.ChannelTitle) ? t.ChannelTitle.Replace(" - Topic", string.Empty, StringComparison.OrdinalIgnoreCase) : "YouTube Music";
                var audioItem = new ChannelItemInfo
                {
                    Id = $"ytm_{t.VideoId}",
                    Name = $"🎵 {artist} — {t.Title}",
                    SeriesName = artist,
                    Artists = new List<string> { artist },
                    Overview = $"🎵 {artist} — {t.Title}\n🔗 https://music.youtube.com/watch?v={t.VideoId}",
                    Type = ChannelItemType.Media,
                    ContentType = ChannelMediaContentType.Song,
                    MediaType = ChannelMediaType.Audio,
                    IndexNumber = idx++,
                    ParentIndexNumber = 1,
                    PremiereDate = t.PublishedAt ?? DateTime.UtcNow.Date,
                    CommunityRating = 8.8f,
                    RunTimeTicks = t.DurationSeconds > 0 ? TimeSpan.FromSeconds(t.DurationSeconds).Ticks : null,
                    ImageUrl = !string.IsNullOrWhiteSpace(t.ThumbnailUrl) ? t.ThumbnailUrl : $"https://i.ytimg.com/vi/{t.VideoId}/hqdefault.jpg"
                };
                YouTubeIdKeys.SetAudioId(audioItem, t.VideoId);
                items.Add(audioItem);
            }

            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        // Standard Category Feeds
        var browseId = query.FolderId switch
        {
            "cat_home" => "FEwhat_to_watch",
            "cat_subscriptions" => "FEsubscriptions",
            "cat_continue" => "FEhistory",
            "cat_watch_later" => "VLWL",
            "cat_liked" => "VLLV",
            "cat_trending" => "FEtrending",
            "cat_gaming" => "FEgaming",
            "cat_music" => "FEmusic",
            "cat_live" => "FElive",
            _ => "FEwhat_to_watch"
        };

        var feedVideos = await client.GetBrowseFeedAsync(browseId, effectiveUserKey, 30, cancellationToken).ConfigureAwait(false);
        var feedItems = await EnrichAndMapVideosAsync(client, feedVideos, cancellationToken).ConfigureAwait(false);
        return new ChannelItemResult { Items = feedItems, TotalRecordCount = feedItems.Count };
    }

    private async Task<List<ChannelItemInfo>> EnrichAndMapVideosAsync(
        YouTubeApiClient client,
        List<YouTubeVideoItem> videos,
        CancellationToken cancellationToken)
    {
        var list = new List<ChannelItemInfo>();
        var config = Config;

        var enrichTargets = videos.Take(12).ToList();
        if (config.EnableReturnYouTubeDislike || config.EnableSponsorBlock)
        {
            var tasks = enrichTargets.Select(async v =>
            {
                if (config.EnableReturnYouTubeDislike && v.DislikeStats == null)
                {
                    v.DislikeStats = await client.GetReturnYouTubeDislikeAsync(v.VideoId, cancellationToken).ConfigureAwait(false);
                }

                if (config.EnableSponsorBlock && v.SponsorSegments.Count == 0)
                {
                    v.SponsorSegments = await client.GetSponsorBlockSegmentsAsync(v.VideoId, cancellationToken).ConfigureAwait(false);
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var idx = 1;
        foreach (var v in videos)
        {
            float? rating = null;
            if (v.DislikeStats != null && (v.DislikeStats.Likes + v.DislikeStats.Dislikes) > 0)
            {
                rating = (float)Math.Round((double)v.DislikeStats.Likes / (v.DislikeStats.Likes + v.DislikeStats.Dislikes) * 10.0, 1);
            }

            var displayTitle = !string.IsNullOrWhiteSpace(v.ChannelTitle) && !v.Title.Contains(v.ChannelTitle, StringComparison.OrdinalIgnoreCase)
                ? $"{v.Title} — {v.ChannelTitle}"
                : v.Title;

            if (v.ResumePercent > 0 && v.ResumePercent < 95)
            {
                displayTitle = $"[▶ {v.ResumePercent}%] {displayTitle}";
            }

            var item = new ChannelItemInfo
            {
                Id = $"yt_{v.VideoId}",
                Name = displayTitle,
                SeriesName = v.ChannelTitle,
                Overview = YouTubeApiClient.FormatRichOverview(v, v.DislikeStats, v.SponsorSegments),
                Type = ChannelItemType.Media,
                ContentType = ChannelMediaContentType.Episode,
                MediaType = ChannelMediaType.Video,
                IndexNumber = idx++,
                ParentIndexNumber = 1,
                PremiereDate = v.PublishedAt,
                CommunityRating = rating,
                RunTimeTicks = v.DurationSeconds > 0 ? TimeSpan.FromSeconds(v.DurationSeconds).Ticks : null,
                ImageUrl = !string.IsNullOrWhiteSpace(v.ThumbnailUrl) ? v.ThumbnailUrl : $"https://i.ytimg.com/vi/{v.VideoId}/hqdefault.jpg"
            };

            YouTubeIdKeys.SetVideoId(item, v.VideoId);
            if (!string.IsNullOrWhiteSpace(v.ChannelId))
            {
                YouTubeIdKeys.SetChannelId(item, v.ChannelId);
            }

            list.Add(item);
        }

        return list;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var isAudio = id.StartsWith("ytm_", StringComparison.Ordinal);
        var videoId = isAudio
            ? id["ytm_".Length..]
            : (id.StartsWith("yt_", StringComparison.Ordinal) ? id["yt_".Length..] : id);
        using var client = CreateClient();
        var streamInfo = await client.ResolveStreamAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (streamInfo == null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var serverUrl = Config.GetPublicServerUrl();
        if (isAudio)
        {
            var bitrate = (Config.YouTubeMusicPreferredBitrate > 0 ? Config.YouTubeMusicPreferredBitrate : 256) * 1000;
            return new[]
            {
                new MediaSourceInfo
                {
                    Id = videoId,
                    Name = $"{streamInfo.Title} (YouTube Music HQ Audio)",
                    Path = $"{serverUrl}/api/youtubePlugin/stream/{videoId}/audio.m4a",
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
                }
            };
        }

        var mediaStreams = new List<MediaStream>
        {
            new()
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                Width = streamInfo.Width > 0 ? streamInfo.Width : 1920,
                Height = streamInfo.Height > 0 ? streamInfo.Height : 1080,
                IsDefault = true
            },
            new()
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                Codec = "aac",
                Language = "und",
                IsDefault = true
            }
        };

        var subIdx = 2;
        foreach (var kv in streamInfo.Subtitles)
        {
            mediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = subIdx++,
                Language = kv.Key,
                Title = $"YouTube ({kv.Key})",
                Codec = "vtt",
                IsExternal = true,
                IsExternalUrl = true,
                Path = kv.Value,
                DeliveryUrl = kv.Value
            });
        }

        var proxyPlayUrl = $"{serverUrl}/api/youtubePlugin/stream/{videoId}/master.m3u8";

        var source = new MediaSourceInfo
        {
            Id = videoId,
            Name = $"{streamInfo.Title} ({streamInfo.Height}p Ad-Free)",
            Path = proxyPlayUrl,
            Protocol = MediaProtocol.Http,
            Container = !string.IsNullOrWhiteSpace(streamInfo.HlsManifestUrl) ? "hls" : "mp4",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RunTimeTicks = streamInfo.DurationSeconds > 0 ? TimeSpan.FromSeconds(streamInfo.DurationSeconds).Ticks : null,
            MediaStreams = mediaStreams
        };

        return new[] { source };
    }

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };
}
