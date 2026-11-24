using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    /// <summary>
    /// Fetches community-submitted clean titles and neutral thumbnails from the DeArrow API.
    /// </summary>
    public async Task<DeArrowBrandingDto?> GetDeArrowBrandingAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        var cacheKey = $"dearrow_{videoId}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalHours < 12)
        {
            return cached.Data as DeArrowBrandingDto;
        }

        try
        {
            var url = $"https://sponsor.ajay.app/api/branding?videoID={Uri.EscapeDataString(videoId)}";
            using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? cleanTitle = null;
            if (root.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in titles.EnumerateArray())
                {
                    var isOriginal = t.TryGetProperty("original", out var origProp) && origProp.ValueKind == JsonValueKind.True;
                    if (isOriginal)
                    {
                        continue;
                    }

                    var rawTitle = t.TryGetProperty("title", out var tp) ? tp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(rawTitle))
                    {
                        cleanTitle = CleanDeArrowTitle(rawTitle);
                        break;
                    }
                }
            }

            string? thumbUrl = null;
            double? thumbTs = null;
            if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
            {
                foreach (var th in thumbs.EnumerateArray())
                {
                    var isOriginal = th.TryGetProperty("original", out var origProp) && origProp.ValueKind == JsonValueKind.True;
                    if (isOriginal)
                    {
                        continue;
                    }

                    if (th.TryGetProperty("timestamp", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number)
                    {
                        thumbTs = tsProp.GetDouble();
                        var tsStr = thumbTs.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        thumbUrl = $"https://dearrow-thumb.ajay.app/api/v1/getThumbnail?videoID={Uri.EscapeDataString(videoId)}&time={tsStr}";
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(cleanTitle) && string.IsNullOrWhiteSpace(thumbUrl))
            {
                return null;
            }

            var dto = new DeArrowBrandingDto
            {
                Title = cleanTitle,
                ThumbnailUrl = thumbUrl,
                ThumbnailTimestamp = thumbTs
            };
            _cache[cacheKey] = (dto, DateTime.UtcNow);
            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DeArrow lookup failed for {VideoId}", videoId);
            return null;
        }
    }

    /// <summary>
    /// Removes DeArrow casual formatting markers (e.g. leading '>' used to preserve capitalization).
    /// </summary>
    public static string CleanDeArrowTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        var parts = rawTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith('>') && parts[i].Length > 1)
            {
                parts[i] = parts[i][1..];
            }
        }

        return string.Join(' ', parts).Trim();
    }

    /// <summary>
    /// Detects whether a video is a YouTube Short based on duration (&lt;= 60s) or #shorts markers.
    /// </summary>
    public static bool IsShortVideo(YouTubeVideoItem video)
    {
        if (video.DurationSeconds > 0 && video.DurationSeconds <= 60)
        {
            return true;
        }

        return (!string.IsNullOrWhiteSpace(video.Title) &&
                (video.Title.Contains("#shorts", StringComparison.OrdinalIgnoreCase) ||
                 video.Title.Contains("/shorts/", StringComparison.OrdinalIgnoreCase))) ||
               (!string.IsNullOrWhiteSpace(video.Description) &&
                video.Description.Contains("#shorts", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks whether a channel ID or title is present in the user's blocked channels CSV.
    /// </summary>
    public static bool IsChannelBlocked(string? channelId, string? channelTitle, string? blockedCsv)
    {
        if (string.IsNullOrWhiteSpace(blockedCsv))
        {
            return false;
        }

        var tokens = blockedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if ((!string.IsNullOrWhiteSpace(channelId) && string.Equals(channelId.Trim(), token, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(channelTitle) && string.Equals(channelTitle.Trim(), token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Filters out Shorts and blocked channels according to plugin configuration.
    /// </summary>
    public static List<YouTubeVideoItem> FilterVideos(IEnumerable<YouTubeVideoItem> videos, PluginConfiguration config)
    {
        var result = new List<YouTubeVideoItem>();
        foreach (var v in videos)
        {
            if (config.HideYouTubeShorts && IsShortVideo(v))
            {
                continue;
            }

            if (IsChannelBlocked(v.ChannelId, v.ChannelTitle, config.BlockedChannels))
            {
                continue;
            }

            result.Add(v);
        }

        return result;
    }

    /// <summary>
    /// Extracts the highest-intensity "Most Replayed" segment from yt-dlp's heatmap array.
    /// </summary>
    public static YouTubeHeatmapPeakDto? ParseHeatmapPeak(JsonElement root)
    {
        if (!root.TryGetProperty("heatmap", out var heatmap) || heatmap.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        YouTubeHeatmapPeakDto? best = null;
        foreach (var entry in heatmap.EnumerateArray())
        {
            var value = entry.TryGetProperty("value", out var vProp) && vProp.ValueKind == JsonValueKind.Number
                ? vProp.GetDouble()
                : 0;
            var start = entry.TryGetProperty("start_time", out var sProp) && sProp.ValueKind == JsonValueKind.Number
                ? sProp.GetDouble()
                : 0;
            var end = entry.TryGetProperty("end_time", out var eProp) && eProp.ValueKind == JsonValueKind.Number
                ? eProp.GetDouble()
                : start;

            if (value >= 0.7 && (best == null || value > best.Intensity))
            {
                best = new YouTubeHeatmapPeakDto
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Intensity = value
                };
            }
        }

        return best;
    }
}
