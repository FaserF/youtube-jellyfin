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
    public async Task<List<YouTubeVideoItem>> GetBrowseFeedAsync(
        string browseId,
        string? usernameOrId,
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"browse_{browseId}_{PluginConfiguration.NormalizeId(usernameOrId)}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalMinutes < 5 &&
            cached.Data is List<YouTubeVideoItem> cachedList)
        {
            return cachedList;
        }

        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        var results = new List<YouTubeVideoItem>();

        if (string.Equals(browseId, "VLLV", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token))
        {
            var liked = await GetLikedVideosApiV3Async(token, maxResults, cancellationToken).ConfigureAwait(false);
            if (liked.Count > 0)
            {
                _cache[cacheKey] = (liked, DateTime.UtcNow);
                return liked;
            }
        }

        if (string.Equals(browseId, "FEtrending", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token))
        {
            var popular = await GetMostPopularVideosApiV3Async(token, maxResults, cancellationToken).ConfigureAwait(false);
            if (popular.Count > 0)
            {
                _cache[cacheKey] = (popular, DateTime.UtcNow);
                return popular;
            }
        }

        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var hl = config.PreferredLanguage.Split('-')[0];
            var gl = config.PreferredRegion;

            var payload = JsonSerializer.Serialize(new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "TVHTML5",
                        clientVersion = "7.20250101.00.00",
                        hl,
                        gl
                    }
                },
                browseId
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                ExtractVideosFromInnertubeJson(doc.RootElement, results, maxResults);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching Innertube browseId {BrowseId} for {User}", browseId, usernameOrId);
        }

        if (results.Count == 0 && string.Equals(browseId, "VLWL", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token))
        {
            var myYt = await GetBrowseFeedAsync("FEmy_youtube", usernameOrId, maxResults, cancellationToken).ConfigureAwait(false);
            results = myYt.Where(v => v.ResumePercent < 90).Take(maxResults).ToList();
        }

        if (results.Count == 0 && string.Equals(browseId, "FEsubscriptions", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token))
        {
            var subs = await GetSubscribedChannelsAsync(usernameOrId, 15, cancellationToken).ConfigureAwait(false);
            var feedTasks = subs.Take(8).Select(s => GetChannelRssFeedAsync(s.ChannelId, 5, cancellationToken));
            var feeds = await Task.WhenAll(feedTasks).ConfigureAwait(false);
            results = feeds.SelectMany(f => f).OrderByDescending(v => v.PublishedAt ?? DateTime.MinValue).Take(maxResults).ToList();
        }

        if (results.Count == 0 && string.IsNullOrWhiteSpace(token))
        {
            var query = browseId switch
            {
                "FEgaming" => "Gaming Deutsch 2026",
                "FEmusic" => "Musik Charts Official Video",
                "FElive" => "Live News Stream Deutsch",
                _ => "Trending Deutsch Technologie Finanzfluss"
            };
            results = await SearchVideosAsync(query, null, maxResults, cancellationToken).ConfigureAwait(false);
        }

        if (results.Count > 0)
        {
            _cache[cacheKey] = (results, DateTime.UtcNow);
        }

        return results;
    }

    public async Task<List<YouTubeChannelItem>> GetSubscribedChannelsAsync(
        string? usernameOrId,
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"subs_{PluginConfiguration.NormalizeId(usernameOrId)}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalMinutes < 10 &&
            cached.Data is List<YouTubeChannelItem> cachedSubs)
        {
            return cachedSubs;
        }

        var list = new List<YouTubeChannelItem>();
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return list;
        }

        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/subscriptions?part=snippet&mine=true&maxResults={Math.Min(50, maxResults)}&order=alphabetical";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("snippet", out var snippet))
                        {
                            continue;
                        }

                        var chanId = snippet.TryGetProperty("resourceId", out var resId) && resId.TryGetProperty("channelId", out var cid)
                            ? cid.GetString() ?? string.Empty
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(chanId))
                        {
                            continue;
                        }

                        var title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? chanId : chanId;
                        var desc = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                        var thumb = ExtractBestThumbnailFromSnippet(snippet);

                        list.Add(new YouTubeChannelItem
                        {
                            ChannelId = chanId,
                            Title = title,
                            Description = desc,
                            ThumbnailUrl = thumb
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching subscribed channels for {User}", usernameOrId);
        }

        if (list.Count > 0)
        {
            _cache[cacheKey] = (list, DateTime.UtcNow);
        }

        return list;
    }

    public async Task<List<YouTubePlaylistItem>> GetUserPlaylistsAsync(
        string? usernameOrId,
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        var list = new List<YouTubePlaylistItem>();
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return list;
        }

        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/playlists?part=snippet,contentDetails&mine=true&maxResults={Math.Min(50, maxResults)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var pid = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(pid) || !item.TryGetProperty("snippet", out var snippet))
                        {
                            continue;
                        }

                        var count = item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("itemCount", out var ic) ? ic.GetInt32() : 0;
                        list.Add(new YouTubePlaylistItem
                        {
                            PlaylistId = pid,
                            Title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? pid : pid,
                            Description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                            ItemCount = count,
                            ThumbnailUrl = ExtractBestThumbnailFromSnippet(snippet)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching user playlists for {User}", usernameOrId);
        }

        return list;
    }

    public async Task<List<YouTubeVideoItem>> GetPlaylistVideosAsync(
        string playlistId,
        string? usernameOrId,
        int maxResults = 30,
        CancellationToken cancellationToken = default)
    {
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false);
        var list = new List<YouTubeVideoItem>();

        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                var url = $"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet,contentDetails&playlistId={Uri.EscapeDataString(playlistId)}&maxResults={Math.Min(50, maxResults)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (!item.TryGetProperty("snippet", out var snippet))
                            {
                                continue;
                            }

                            var vid = snippet.TryGetProperty("resourceId", out var rid) && rid.TryGetProperty("videoId", out var vProp)
                                ? vProp.GetString() ?? string.Empty
                                : string.Empty;
                            if (string.IsNullOrWhiteSpace(vid))
                            {
                                continue;
                            }

                            list.Add(new YouTubeVideoItem
                            {
                                VideoId = vid,
                                Title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? vid : vid,
                                Description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                                ChannelTitle = snippet.TryGetProperty("videoOwnerChannelTitle", out var ct) ? ct.GetString() ?? string.Empty : string.Empty,
                                ChannelId = snippet.TryGetProperty("videoOwnerChannelId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                                ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg"
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Data API playlistItems failed for {PlaylistId}, falling back to Innertube", playlistId);
            }
        }

        if (list.Count == 0)
        {
            var browseId = playlistId.StartsWith("VL", StringComparison.OrdinalIgnoreCase) ? playlistId : "VL" + playlistId;
            return await GetBrowseFeedAsync(browseId, usernameOrId, maxResults, cancellationToken).ConfigureAwait(false);
        }

        return list;
    }

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

    private async Task<List<YouTubeVideoItem>> GetMostPopularVideosApiV3Async(string accessToken, int maxResults, CancellationToken cancellationToken)
    {
        var list = new List<YouTubeVideoItem>();
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails,statistics&chart=mostPopular&regionCode={config.PreferredRegion}&maxResults={Math.Min(50, maxResults)}";
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
