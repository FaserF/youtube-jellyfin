using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    private const string IosInnertubeUserAgent =
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_1 like Mac OS X;)";

    /// <summary>
    /// Fast-path stream resolution via YouTube Innertube /youtubei/v1/player (~150-300ms)
    /// avoiding yt-dlp subprocess startup overhead on standard public/unrestricted videos.
    /// </summary>
    public async Task<YouTubeStreamInfoDto?> TryResolveViaInnertubeAsync(
        string videoId,
        PluginConfiguration config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var hl = (config.PreferredLanguage ?? "en").Split('-')[0];
            var gl = !string.IsNullOrWhiteSpace(config.PreferredRegion) ? config.PreferredRegion : "US";

            var payload = JsonSerializer.Serialize(new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "IOS",
                        clientVersion = "20.10.4",
                        deviceMake = "Apple",
                        deviceModel = "iPhone16,2",
                        osName = "iOS",
                        osVersion = "18.3.1.22D72",
                        hl,
                        gl
                    }
                },
                videoId,
                contentCheckOk = true,
                racyCheckOk = true
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", IosInnertubeUserAgent);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var resp = await _httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("playabilityStatus", out var ps) &&
                ps.TryGetProperty("status", out var statusProp))
            {
                var status = statusProp.GetString();
                if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (!root.TryGetProperty("streamingData", out var sd))
            {
                return null;
            }

            var hlsUrl = sd.TryGetProperty("hlsManifestUrl", out var hlsProp) ? hlsProp.GetString() ?? string.Empty : string.Empty;
            string? bestCombinedUrl = null;
            string? bestAudioUrl = null;
            var bestCombinedHeight = 0;
            var bestAudioBitrate = 0;
            var videoFormats = new List<YouTubeVideoFormatDto>();
            var audioTracks = new List<YouTubeAudioTrackDto>();
            var maxHeight = config.PreferredMaxResolution > 0 ? config.PreferredMaxResolution : 2160;

            if (sd.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fmts.EnumerateArray())
                {
                    var url = f.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var h = f.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number ? hProp.GetInt32() : 0;
                    var w = f.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number ? wProp.GetInt32() : 0;
                    var br = f.TryGetProperty("bitrate", out var bProp) && bProp.ValueKind == JsonValueKind.Number ? bProp.GetInt64() : 0;
                    if (h <= maxHeight && h >= bestCombinedHeight)
                    {
                        bestCombinedHeight = h;
                        bestCombinedUrl = url;
                    }

                    if (h > 0 && h <= maxHeight)
                    {
                        videoFormats.Add(new YouTubeVideoFormatDto
                        {
                            Height = h,
                            Width = w > 0 ? w : h * 16 / 9,
                            Codec = "h264",
                            Bitrate = br,
                            Fps = f.TryGetProperty("fps", out var fpsProp) && fpsProp.ValueKind == JsonValueKind.Number ? (float)fpsProp.GetDouble() : 30f,
                            StreamUrl = url,
                            Label = $"{h}p"
                        });
                    }
                }
            }

            if (sd.TryGetProperty("adaptiveFormats", out var afmts) && afmts.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in afmts.EnumerateArray())
                {
                    var url = f.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    var mime = f.TryGetProperty("mimeType", out var mProp) ? mProp.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(url) || !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var br = f.TryGetProperty("bitrate", out var bProp) && bProp.ValueKind == JsonValueKind.Number ? bProp.GetInt32() : 128000;
                    var isAac = mime.Contains("mp4a", StringComparison.OrdinalIgnoreCase);
                    if (br > bestAudioBitrate || (isAac && bestAudioUrl == null))
                    {
                        bestAudioBitrate = br;
                        bestAudioUrl = url;
                    }

                    audioTracks.Add(new YouTubeAudioTrackDto
                    {
                        Codec = isAac ? "aac" : "opus",
                        Bitrate = br,
                        StreamUrl = url,
                        Language = "und",
                        Title = isAac ? "AAC Audio" : "Opus Audio",
                        IsDefault = audioTracks.Count == 0
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(hlsUrl) && string.IsNullOrWhiteSpace(bestCombinedUrl))
            {
                return null;
            }

            var info = new YouTubeStreamInfoDto
            {
                VideoId = videoId,
                HlsManifestUrl = hlsUrl,
                DirectStreamUrl = bestCombinedUrl ?? hlsUrl,
                AudioStreamUrl = bestAudioUrl ?? bestCombinedUrl ?? string.Empty,
                Height = bestCombinedHeight > 0 ? bestCombinedHeight : 1080,
                Width = bestCombinedHeight > 0 ? bestCombinedHeight * 16 / 9 : 1920,
                VideoFormats = videoFormats.OrderByDescending(v => v.Height).ToList(),
                AudioTracks = audioTracks.OrderByDescending(a => a.Codec == "aac").ThenByDescending(a => a.Bitrate).ToList()
            };

            if (root.TryGetProperty("videoDetails", out var vd))
            {
                info.Title = vd.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? videoId : videoId;
                info.ChannelTitle = vd.TryGetProperty("author", out var aProp) ? aProp.GetString() ?? string.Empty : string.Empty;
                info.ChannelId = vd.TryGetProperty("channelId", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                info.Description = vd.TryGetProperty("shortDescription", out var dProp) ? dProp.GetString() ?? string.Empty : string.Empty;
                if (vd.TryGetProperty("lengthSeconds", out var lProp) && long.TryParse(lProp.GetString(), out var secs))
                {
                    info.DurationSeconds = secs;
                }
            }

            if (root.TryGetProperty("captions", out var caps) &&
                caps.TryGetProperty("playerCaptionsTracklistRenderer", out var pctr) &&
                pctr.TryGetProperty("captionTracks", out var cTracks) &&
                cTracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var ct in cTracks.EnumerateArray())
                {
                    var lang = ct.TryGetProperty("languageCode", out var lc) ? lc.GetString() : null;
                    var baseUrl = ct.TryGetProperty("baseUrl", out var bu) ? bu.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(lang) && !string.IsNullOrWhiteSpace(baseUrl))
                    {
                        info.Subtitles.TryAdd(lang, baseUrl);
                        info.SubtitleTracks.Add(new YouTubeSubtitleTrackDto
                        {
                            Language = lang,
                            Title = lang,
                            StreamUrl = baseUrl,
                            IsAutoGenerated = ct.TryGetProperty("kind", out var kProp) && string.Equals(kProp.GetString(), "asr", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Innertube fast-path failed for {VideoId}, falling back to yt-dlp", videoId);
            return null;
        }
    }
}
