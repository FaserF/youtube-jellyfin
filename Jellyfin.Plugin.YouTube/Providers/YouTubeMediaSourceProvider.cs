using System.Collections.Concurrent;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
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
/// Returns one MediaSourceInfo per available resolution (360p–4K) so clients can pick quality.
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
            return new[] { cached.Source };
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
                return new[] { audioSource };
            }

            var width = streamInfo?.Width > 0 ? streamInfo.Width : 1920;
            var height = streamInfo?.Height > 0 ? streamInfo.Height : 1080;
            var runTimeTicks = streamInfo?.DurationSeconds > 0 ? TimeSpan.FromSeconds(streamInfo.DurationSeconds).Ticks : item.RunTimeTicks;
            var prefLang = (config.PreferredLanguage ?? "de").Split('-')[0];

            // Build shared audio + subtitle streams (re-indexed starting from 1)
            var (audioStreams, defaultAudioStreamIndex) = BuildAudioStreams(streamInfo, prefLang);
            var audioCount = audioStreams.Count;
            var (subtitleStreams, defaultSubStreamIndex) = BuildSubtitleStreams(streamInfo, prefLang, videoId, internalBaseUrl, 1 + audioCount);

            // C: One MediaSourceInfo per available resolution (sorted highest first)
            var allSources = new List<MediaSourceInfo>();
            if (streamInfo?.VideoFormats is { Count: > 0 } videoFormats)
            {
                foreach (var fmt in videoFormats)
                {
                    var streams = new List<MediaStream>
                    {
                        new()
                        {
                            Type = MediaStreamType.Video,
                            Index = 0,
                            Codec = fmt.Codec,
                            Width = fmt.Width > 0 ? fmt.Width : fmt.Height * 16 / 9,
                            Height = fmt.Height,
                            BitRate = (int)Math.Min(fmt.Bitrate, int.MaxValue),
                            Title = fmt.Label,
                            AspectRatio = "16:9",
                            AverageFrameRate = fmt.Fps > 0 ? fmt.Fps : 30f,
                            RealFrameRate = fmt.Fps > 0 ? fmt.Fps : 30f,
                            IsDefault = true
                        }
                    };
                    streams.AddRange(audioStreams);
                    streams.AddRange(subtitleStreams);

                    allSources.Add(new MediaSourceInfo
                    {
                        Id = $"{item.Id:N}_{fmt.Height}",
                        Name = $"{item.Name} ({fmt.Label})",
                        Path = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/master.m3u8",
                        Protocol = MediaProtocol.Http,
                        Container = "m3u8",
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = true,
                        SupportsProbing = false,
                        DefaultAudioStreamIndex = defaultAudioStreamIndex,
                        DefaultSubtitleStreamIndex = defaultSubStreamIndex,
                        RunTimeTicks = runTimeTicks,
                        MediaStreams = streams
                    });
                }
            }

            // Fallback: single source when no discrete VideoFormats available (HLS-only stream)
            if (allSources.Count == 0)
            {
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
                streams.AddRange(audioStreams);
                streams.AddRange(subtitleStreams);

                allSources.Add(new MediaSourceInfo
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
                    RunTimeTicks = runTimeTicks,
                    MediaStreams = streams
                });
            }

            _sourceCache[cacheKey] = (allSources[0], DateTime.UtcNow);
            return allSources;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTubeMediaSourceProvider failed for {VideoId}", videoId);
            return Enumerable.Empty<MediaSourceInfo>();
        }
    }

    private static (List<MediaStream> Streams, int DefaultIndex) BuildAudioStreams(
        YouTubeStreamInfoDto? streamInfo,
        string prefLang)
    {
        var streams = new List<MediaStream>();
        var defaultIndex = 1;
        var idx = 1;

        if (streamInfo?.AudioTracks is { Count: > 0 } tracks)
        {
            var hasDefault = false;
            foreach (var a in tracks)
            {
                var isDefault = !hasDefault && (
                    a.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase) ||
                    (a.IsDefault && !tracks.Any(at => at.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase))));
                var curIdx = idx++;
                if (isDefault)
                {
                    hasDefault = true;
                    defaultIndex = curIdx;
                }

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = curIdx,
                    Codec = a.Codec,
                    Channels = a.Channels > 0 ? a.Channels : 2,
                    SampleRate = a.SampleRate > 0 ? a.SampleRate : 44100,
                    BitRate = a.Bitrate > 0 ? a.Bitrate : 128000,
                    Language = !string.IsNullOrWhiteSpace(a.Language) ? a.Language : "und",
                    Title = a.Title,
                    IsDefault = isDefault
                });
            }
        }
        else
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = idx,
                Codec = "aac",
                Channels = 2,
                SampleRate = 44100,
                BitRate = 128000,
                Language = "und",
                Title = "YouTube Audio",
                IsDefault = true
            });
        }

        return (streams, defaultIndex);
    }

    private static (List<MediaStream> Streams, int? DefaultIndex) BuildSubtitleStreams(
        YouTubeStreamInfoDto? streamInfo,
        string prefLang,
        string videoId,
        string internalBaseUrl,
        int startIdx)
    {
        var streams = new List<MediaStream>();
        int? defaultIdx = null;
        var idx = startIdx;

        if (streamInfo?.SubtitleTracks is { Count: > 0 } subTracks)
        {
            foreach (var st in subTracks)
            {
                var subUrl = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/subtitles/{Uri.EscapeDataString(st.Language)}.vtt";
                var isDefault = defaultIdx == null &&
                                st.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase) &&
                                !st.IsAutoGenerated;
                var curIdx = idx++;
                if (isDefault)
                {
                    defaultIdx = curIdx;
                }

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = curIdx,
                    Language = st.Language,
                    Title = st.Title,
                    Codec = "vtt",
                    IsDefault = isDefault,
                    IsExternal = true,
                    IsExternalUrl = false,
                    DeliveryMethod = MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.External,
                    Path = subUrl,
                    DeliveryUrl = subUrl
                });
            }
        }
        else if (streamInfo?.Subtitles is { Count: > 0 } subtitles)
        {
            foreach (var kv in subtitles)
            {
                var subUrl = $"{internalBaseUrl}/api/youtubePlugin/stream/{videoId}/subtitles/{Uri.EscapeDataString(kv.Key)}.vtt";
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = idx++,
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

        return (streams, defaultIdx);
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken) =>
        Task.FromResult<ILiveStream>(null!);
}
