using System.Collections.Concurrent;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

/// <summary>
/// Dynamic MediaSource provider for YouTube &amp; YouTube Music .strm and library items.
/// Supplies playable MediaSourceInfo with pre-populated video, audio, and subtitle streams
/// so Android TV, Fire TV, Web, and Mobile clients play immediately without FFmpeg probing delays.
/// </summary>
public class YouTubeMediaSourceProvider : IMediaSourceProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeMediaSourceProvider> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public int Order => -100;

    private static readonly ConcurrentDictionary<string, (MediaSourceInfo Source, DateTime CachedAt)> _sourceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    public YouTubeMediaSourceProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeMediaSourceProvider> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Episode && item is not Movie && item is not Audio)
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        var isAudioItem = item is Audio || (item.Path != null && item.Path.Contains("/audio.m4a", StringComparison.OrdinalIgnoreCase));
        var cacheKey = isAudioItem ? $"audio_{videoId}" : $"video_{videoId}";

        if (_sourceCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
        {
            return new[] { CloneMediaSource(cached.Source, item.Id.ToString("N")) };
        }

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            const string internalBaseUrl = "http://127.0.0.1:8096";

            using var client = new YouTubeApiClient(
                _httpClientFactory.CreateClient("YouTube"),
                _loggerFactory.CreateLogger<YouTubeApiClient>());

            var streamInfo = await client.ResolveStreamAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (isAudioItem)
            {
                var bitrate = (config.YouTubeMusicPreferredBitrate > 0 ? config.YouTubeMusicPreferredBitrate : 256) * 1000;
                var audioSource = new MediaSourceInfo
                {
                    Id = item.Id.ToString("N"),
                    Name = $"{item.Name} (YouTube Music HQ)",
                    Path = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/audio.m4a",
                    Protocol = MediaProtocol.Http,
                    Container = "m4a",
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = true,
                    SupportsProbing = false,
                    DefaultAudioStreamIndex = 0,
                    RunTimeTicks = streamInfo?.DurationSeconds > 0 ? TimeSpan.FromSeconds(streamInfo.DurationSeconds).Ticks : item.RunTimeTicks,
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

                _sourceCache[cacheKey] = (audioSource, DateTime.UtcNow);
                return new[] { CloneMediaSource(audioSource, item.Id.ToString("N")) };
            }

            var width = streamInfo?.Width > 0 ? streamInfo.Width : 1920;
            var height = streamInfo?.Height > 0 ? streamInfo.Height : 1080;

            var streams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = width,
                    Height = height,
                    BitRate = 8000000,
                    Title = $"{height}p Ad-Free (YouTube)",
                    AspectRatio = "16:9",
                    AverageFrameRate = 30f,
                    RealFrameRate = 30f,
                    IsDefault = true
                }
            };

            var streamIdx = 1;
            var defaultAudioStreamIndex = 1;

            // Audio Tracks (Multi-language Audio Dubs & Original Tracks parity with SmartTube)
            if (streamInfo?.AudioTracks != null && streamInfo.AudioTracks.Count > 0)
            {
                var prefLang = config.PreferredLanguage ?? "de";
                var hasExplicitDefault = false;

                foreach (var a in streamInfo.AudioTracks)
                {
                    var isDefaultTrack = !hasExplicitDefault && (
                        a.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase) ||
                        (a.IsDefault && !streamInfo.AudioTracks.Any(at => at.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase)))
                    );

                    var currentIdx = streamIdx++;
                    if (isDefaultTrack)
                    {
                        hasExplicitDefault = true;
                        defaultAudioStreamIndex = currentIdx;
                    }

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = currentIdx,
                        Codec = a.Codec,
                        Channels = a.Channels > 0 ? a.Channels : 2,
                        SampleRate = a.SampleRate > 0 ? a.SampleRate : 44100,
                        BitRate = a.Bitrate > 0 ? a.Bitrate : 128000,
                        Language = !string.IsNullOrWhiteSpace(a.Language) ? a.Language : "und",
                        Title = a.Title,
                        IsDefault = isDefaultTrack
                    });
                }
            }
            else
            {
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = streamIdx++,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 44100,
                    BitRate = 128000,
                    Language = "und",
                    Title = "YouTube Audio",
                    IsDefault = true
                });
            }

            // Subtitle Tracks (Official & Auto-Generated Captions parity with SmartTube)
            int? defaultSubStreamIndex = null;
            if (streamInfo?.SubtitleTracks != null && streamInfo.SubtitleTracks.Count > 0)
            {
                var prefLang = config.PreferredLanguage ?? "de";
                foreach (var st in streamInfo.SubtitleTracks)
                {
                    var subUrl = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/subtitles/{Uri.EscapeDataString(st.Language)}.vtt";
                    var isDefaultSub = defaultSubStreamIndex == null && st.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase) && !st.IsAutoGenerated;
                    var currentIdx = streamIdx++;
                    if (isDefaultSub)
                    {
                        defaultSubStreamIndex = currentIdx;
                    }

                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = currentIdx,
                        Language = st.Language,
                        Title = st.Title,
                        Codec = "vtt",
                        IsDefault = isDefaultSub,
                        IsExternal = true,
                        IsExternalUrl = false,
                        DeliveryMethod = MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.External,
                        Path = subUrl,
                        DeliveryUrl = subUrl
                    });
                }
            }
            else if (streamInfo?.Subtitles != null)
            {
                foreach (var kv in streamInfo.Subtitles)
                {
                    var subUrl = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/subtitles/{Uri.EscapeDataString(kv.Key)}.vtt";
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = streamIdx++,
                        Language = kv.Key,
                        Title = $"YouTube ({kv.Key})",
                        Codec = "vtt",
                        IsExternal = true,
                        IsExternalUrl = false,
                        DeliveryMethod = MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.External,
                        Path = subUrl,
                        DeliveryUrl = subUrl
                    });
                }
            }

            var source = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Name = $"{item.Name} ({height}p Ad-Free)",
                Path = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/master.m3u8",
                Protocol = MediaProtocol.Http,
                Container = "m3u8",
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                DefaultAudioStreamIndex = defaultAudioStreamIndex,
                DefaultSubtitleStreamIndex = defaultSubStreamIndex,
                RunTimeTicks = streamInfo?.DurationSeconds > 0 ? TimeSpan.FromSeconds(streamInfo.DurationSeconds).Ticks : item.RunTimeTicks,
                MediaStreams = streams
            };

            _sourceCache[cacheKey] = (source, DateTime.UtcNow);
            return new[] { CloneMediaSource(source, item.Id.ToString("N")) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTubeMediaSourceProvider failed for {VideoId}", videoId);
            return Enumerable.Empty<MediaSourceInfo>();
        }
    }

    private static MediaSourceInfo CloneMediaSource(MediaSourceInfo src, string id)
    {
        return new MediaSourceInfo
        {
            Id = id,
            Name = src.Name,
            Path = src.Path,
            Protocol = src.Protocol,
            Container = src.Container,
            SupportsDirectPlay = src.SupportsDirectPlay,
            SupportsDirectStream = src.SupportsDirectStream,
            SupportsTranscoding = src.SupportsTranscoding,
            SupportsProbing = src.SupportsProbing,
            IsRemote = src.IsRemote,
            DefaultAudioStreamIndex = src.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = src.DefaultSubtitleStreamIndex,
            RunTimeTicks = src.RunTimeTicks,
            MediaStreams = src.MediaStreams?.Select(s => new MediaStream
            {
                Type = s.Type,
                Index = s.Index,
                Codec = s.Codec,
                Width = s.Width,
                Height = s.Height,
                BitRate = s.BitRate,
                Channels = s.Channels,
                SampleRate = s.SampleRate,
                Language = s.Language,
                Title = s.Title,
                AspectRatio = s.AspectRatio,
                AverageFrameRate = s.AverageFrameRate,
                RealFrameRate = s.RealFrameRate,
                IsDefault = s.IsDefault,
                IsExternal = s.IsExternal,
                IsExternalUrl = s.IsExternalUrl,
                DeliveryMethod = s.DeliveryMethod,
                Path = s.Path,
                DeliveryUrl = s.DeliveryUrl
            }).ToList()
        };
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);
}
