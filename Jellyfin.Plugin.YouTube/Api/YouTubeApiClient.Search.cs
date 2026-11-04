using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    public async Task<List<YouTubeVideoItem>> GetChannelRssFeedAsync(
        string channelId,
        int maxResults = 15,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"rss_{channelId}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalMinutes < 10 &&
            cached.Data is List<YouTubeVideoItem> cachedRss)
        {
            return cachedRss.Take(maxResults).ToList();
        }

        var list = new List<YouTubeVideoItem>();
        try
        {
            var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}";
            var xml = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var chanTitleMatch = Regex.Match(xml, @"<author>\s*<name>([^<]+)</name>");
            var channelTitle = chanTitleMatch.Success ? WebUtility.HtmlDecode(chanTitleMatch.Groups[1].Value) : channelId;

            var entryMatches = Regex.Matches(xml, @"<entry>([\s\S]*?)</entry>");
            foreach (Match m in entryMatches)
            {
                if (list.Count >= maxResults)
                {
                    break;
                }

                var block = m.Groups[1].Value;
                var vid = Regex.Match(block, @"<yt:videoId>([^<]+)</yt:videoId>").Groups[1].Value;
                if (string.IsNullOrWhiteSpace(vid))
                {
                    continue;
                }

                var title = WebUtility.HtmlDecode(Regex.Match(block, @"<title>([\s\S]*?)</title>").Groups[1].Value);
                var desc = WebUtility.HtmlDecode(Regex.Match(block, @"<media:description>([\s\S]*?)</media:description>").Groups[1].Value);
                var pubStr = Regex.Match(block, @"<published>([^<]+)</published>").Groups[1].Value;
                var viewsStr = Regex.Match(block, @"<media:statistics views=""(\d+)""").Groups[1].Value;

                DateTime? pubDate = DateTime.TryParse(pubStr, out var dt) ? dt.ToUniversalTime() : null;
                long.TryParse(viewsStr, out var views);

                list.Add(new YouTubeVideoItem
                {
                    VideoId = vid,
                    Title = title,
                    ChannelId = channelId,
                    ChannelTitle = channelTitle,
                    Description = desc,
                    ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
                    ViewCount = views,
                    PublishedAt = pubDate,
                    PublishedTimeText = pubDate.HasValue ? pubDate.Value.ToString("yyyy-MM-dd HH:mm") : string.Empty
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RSS feed fetch failed for channel {ChannelId}", channelId);
        }

        if (list.Count > 0)
        {
            _cache[cacheKey] = (list, DateTime.UtcNow);
        }

        return list;
    }

    public async Task<List<YouTubeVideoItem>> SearchVideosAsync(
        string query,
        string? usernameOrId,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var list = new List<YouTubeVideoItem>();
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB",
                        clientVersion = "2.20250101.00.00",
                        hl = "de",
                        gl = "DE"
                    }
                },
                query
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                ExtractVideosFromInnertubeJson(doc.RootElement, list, maxResults);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube search failed for query '{Query}'", query);
        }

        return list;
    }

    private async Task<List<YouTubeVideoItem>> GetLikedVideosApiV3Async(string accessToken, int maxResults, CancellationToken cancellationToken)
    {
        var list = new List<YouTubeVideoItem>();
        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails,statistics&myRating=like&maxResults={Math.Min(50, maxResults)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                ParseDataApiV3VideoItems(doc.RootElement, list);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch liked videos via Data API v3");
        }

        return list;
    }

    private async Task<List<YouTubeVideoItem>> GetMostPopularVideosApiV3Async(string accessToken, int maxResults, CancellationToken cancellationToken, string? videoCategoryId = null)
    {
        var list = new List<YouTubeVideoItem>();
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var catParam = !string.IsNullOrWhiteSpace(videoCategoryId) ? $"&videoCategoryId={Uri.EscapeDataString(videoCategoryId)}" : string.Empty;
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails,statistics&chart=mostPopular&regionCode={config.PreferredRegion}{catParam}&maxResults={Math.Min(50, maxResults)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                ParseDataApiV3VideoItems(doc.RootElement, list);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch mostPopular videos via Data API v3");
        }

        return list;
    }
}
