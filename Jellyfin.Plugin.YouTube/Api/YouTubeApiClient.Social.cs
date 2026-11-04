using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    public async Task<List<SponsorBlockSegmentDto>> GetSponsorBlockSegmentsAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return new List<SponsorBlockSegmentDto>();
        }

        if (_sbCache.TryGetValue(videoId, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalHours < 6)
        {
            return cached.Segments;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableSponsorBlock)
        {
            return new List<SponsorBlockSegmentDto>();
        }

        try
        {
            var cats = (config.SponsorBlockCategories ?? "sponsor,selfpromo,interaction,intro,outro")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var catJson = JsonSerializer.Serialize(cats);
            var url = $"https://sponsor.ajay.app/api/skipSegments?videoID={Uri.EscapeDataString(videoId)}&categories={Uri.EscapeDataString(catJson)}";

            using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var segments = JsonSerializer.Deserialize<List<SponsorBlockSegmentDto>>(json) ?? new List<SponsorBlockSegmentDto>();
                _sbCache[videoId] = (segments, DateTime.UtcNow);
                return segments;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SponsorBlock lookup failed for {VideoId}", videoId);
        }

        var empty = new List<SponsorBlockSegmentDto>();
        _sbCache[videoId] = (empty, DateTime.UtcNow);
        return empty;
    }

    public async Task<ReturnYouTubeDislikeDto?> GetReturnYouTubeDislikeAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        if (_rydCache.TryGetValue(videoId, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalHours < 6)
        {
            return cached.Stats;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableReturnYouTubeDislike)
        {
            return null;
        }

        try
        {
            var url = $"https://returnyoutubedislikeapi.com/votes?videoId={Uri.EscapeDataString(videoId)}";
            using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var stats = JsonSerializer.Deserialize<ReturnYouTubeDislikeDto>(json);
                if (stats != null)
                {
                    _rydCache[videoId] = (stats, DateTime.UtcNow);
                    return stats;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReturnYouTubeDislike lookup failed for {VideoId}", videoId);
        }

        return null;
    }

    public async Task<bool> RateVideoAsync(
        string videoId,
        string rating,
        string? usernameOrId,
        CancellationToken cancellationToken = default)
    {
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/videos/rate?id={Uri.EscapeDataString(videoId)}&rating={Uri.EscapeDataString(rating)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rate video {VideoId} ({Rating}) for {User}", videoId, rating, usernameOrId);
            return false;
        }
    }

    public async Task<bool> SubscribeChannelAsync(
        string channelId,
        string? usernameOrId,
        CancellationToken cancellationToken = default)
    {
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                snippet = new
                {
                    resourceId = new
                    {
                        kind = "youtube#channel",
                        channelId
                    }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/youtube/v3/subscriptions?part=snippet")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            _cache.TryRemove($"subs_{PluginConfiguration.NormalizeId(usernameOrId)}", out _);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to channel {ChannelId} for {User}", channelId, usernameOrId);
            return false;
        }
    }
}
