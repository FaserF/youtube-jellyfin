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

        if (string.Equals(browseId, "FEmusic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(token))
        {
            var popularMusic = await GetMostPopularVideosApiV3Async(token, maxResults, cancellationToken, "10").ConfigureAwait(false);
            if (popularMusic.Count > 0)
            {
                _cache[cacheKey] = (popularMusic, DateTime.UtcNow);
                return popularMusic;
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
                "FEgaming" => "Gaming 2026",
                "FEmusic" => "Music Charts Official Video",
                "FElive" => "Live News Stream",
                _ => "Trending Technology News"
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
            // 1. Try Innertube FEsubscriptions TV tabs (works directly with TV token, zero Google Cloud quota)
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
                browseId = "FEsubscriptions"
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("contents", out var contents) &&
                    contents.TryGetProperty("tvBrowseRenderer", out var tb) &&
                    tb.TryGetProperty("content", out var tc) &&
                    tc.TryGetProperty("tvSecondaryNavRenderer", out var nav) &&
                    nav.TryGetProperty("sections", out var sections) &&
                    sections.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sec in sections.EnumerateArray())
                    {
                        if (sec.TryGetProperty("tvSecondaryNavSectionRenderer", out var snr) &&
                            snr.TryGetProperty("tabs", out var tabs) &&
                            tabs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tab in tabs.EnumerateArray())
                            {
                                if (list.Count >= maxResults)
                                {
                                    break;
                                }

                                if (!tab.TryGetProperty("tabRenderer", out var tr))
                                {
                                    continue;
                                }

                                var title = tr.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                                if (string.IsNullOrWhiteSpace(title) || title == "Alle" || title == "A–Z")
                                {
                                    continue;
                                }

                                if (tr.TryGetProperty("endpoint", out var ep) &&
                                    ep.TryGetProperty("browseEndpoint", out var be) &&
                                    be.TryGetProperty("params", out var pProp))
                                {
                                    var paramStr = pProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(paramStr))
                                    {
                                        try
                                        {
                                            var unescaped = WebUtility.UrlDecode(paramStr);
                                            var bytes = Convert.FromBase64String(unescaped);
                                            var raw = Encoding.ASCII.GetString(bytes);
                                            var m = Regex.Match(raw, @"(UC[a-zA-Z0-9_-]{22})");
                                            if (m.Success)
                                            {
                                                var chanId = m.Groups[1].Value;
                                                if (!list.Any(c => c.ChannelId == chanId))
                                                {
                                                    list.Add(new YouTubeChannelItem
                                                    {
                                                        ChannelId = chanId,
                                                        Title = title,
                                                        Description = $"YouTube Channel: {title}",
                                                        ThumbnailUrl = $"https://i.ytimg.com/i/{chanId}/1.jpg"
                                                    });
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Skip non-base64 params
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Innertube subscriptions fetch failed for {User}", usernameOrId);
        }

        // 2. Fallback to Google Data API v3 if Innertube returned nothing
        if (list.Count == 0)
        {
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
                _logger.LogWarning(ex, "Error fetching subscribed channels from API v3 for {User}", usernameOrId);
            }
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
}
