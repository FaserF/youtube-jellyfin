using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    public async Task<YouTubeVideoItem?> GetVideoMetadataAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"vidmeta_{videoId}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.Data is YouTubeVideoItem cachedVideo &&
            DateTime.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(30))
        {
            return cachedVideo;
        }

        var token = await EnsureUserAccessTokenAsync("fabian", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                var url = $"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails,statistics&id={Uri.EscapeDataString(videoId)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    {
                        var el = items[0];
                        var snippet = el.GetProperty("snippet");
                        var item = new YouTubeVideoItem
                        {
                            VideoId = videoId,
                            Title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? videoId : videoId,
                            Description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                            ChannelId = snippet.TryGetProperty("channelId", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                            ChannelTitle = snippet.TryGetProperty("channelTitle", out var ct) ? ct.GetString() ?? string.Empty : string.Empty,
                            ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg"
                        };

                        if (snippet.TryGetProperty("publishedAt", out var pubProp) && DateTime.TryParse(pubProp.GetString(), out var pubDt))
                        {
                            item.PublishedAt = pubDt;
                            item.PublishedTimeText = pubDt.ToString("yyyy-MM-dd");
                        }

                        if (el.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("duration", out var durProp))
                        {
                            try
                            {
                                item.DurationSeconds = (long)System.Xml.XmlConvert.ToTimeSpan(durProp.GetString() ?? "PT0S").TotalSeconds;
                            }
                            catch
                            {
                                // Ignore ISO duration parse errors
                            }
                        }

                        if (el.TryGetProperty("statistics", out var stats) && stats.TryGetProperty("viewCount", out var vcProp) && long.TryParse(vcProp.GetString(), out var vc))
                        {
                            item.ViewCount = vc;
                        }

                        _cache[cacheKey] = (item, DateTime.UtcNow);
                        return item;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "YouTube Data API v3 video metadata lookup failed for {VideoId}", videoId);
            }
        }

        try
        {
            var oembedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}&format=json";
            var json = await _httpClient.GetStringAsync(oembedUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fallbackItem = new YouTubeVideoItem
            {
                VideoId = videoId,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? videoId : videoId,
                ChannelTitle = root.TryGetProperty("author_name", out var a) ? a.GetString() ?? string.Empty : string.Empty,
                ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
            };
            _cache[cacheKey] = (fallbackItem, DateTime.UtcNow);
            return fallbackItem;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "YouTube oEmbed metadata lookup failed for {VideoId}", videoId);
            return null;
        }
    }

    public async Task<YouTubeChannelItem?> GetChannelMetadataAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"chanmeta_{channelId}";
        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.Data is YouTubeChannelItem cachedChan &&
            DateTime.UtcNow - cached.CachedAt < TimeSpan.FromMinutes(30))
        {
            return cachedChan;
        }

        var token = await EnsureUserAccessTokenAsync("fabian", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                var url = $"https://www.googleapis.com/youtube/v3/channels?part=snippet,brandingSettings,statistics&id={Uri.EscapeDataString(channelId)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                    {
                        var el = items[0];
                        var snippet = el.GetProperty("snippet");
                        var thumb = string.Empty;
                        if (snippet.TryGetProperty("thumbnails", out var thumbs))
                        {
                            if (thumbs.TryGetProperty("high", out var hi) && hi.TryGetProperty("url", out var hu))
                            {
                                thumb = hu.GetString() ?? string.Empty;
                            }
                            else if (thumbs.TryGetProperty("default", out var def) && def.TryGetProperty("url", out var du))
                            {
                                thumb = du.GetString() ?? string.Empty;
                            }
                        }

                        var banner = string.Empty;
                        if (el.TryGetProperty("brandingSettings", out var bs) &&
                            bs.TryGetProperty("image", out var img) &&
                            img.TryGetProperty("bannerExternalUrl", out var bProp))
                        {
                            banner = bProp.GetString() ?? string.Empty;
                        }

                        var ch = new YouTubeChannelItem
                        {
                            ChannelId = channelId,
                            Title = snippet.TryGetProperty("title", out var t) ? t.GetString() ?? channelId : channelId,
                            Description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                            ThumbnailUrl = thumb,
                            BannerUrl = banner
                        };
                        _cache[cacheKey] = (ch, DateTime.UtcNow);
                        return ch;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "YouTube Data API v3 channel lookup failed for {ChannelId}", channelId);
            }
        }

        var rssVideos = await GetChannelRssFeedAsync(channelId, 1, cancellationToken).ConfigureAwait(false);
        if (rssVideos.Count > 0)
        {
            var fallbackCh = new YouTubeChannelItem
            {
                ChannelId = channelId,
                Title = rssVideos[0].ChannelTitle,
                Description = $"YouTube Channel: {rssVideos[0].ChannelTitle}",
                ThumbnailUrl = rssVideos[0].ThumbnailUrl
            };
            _cache[cacheKey] = (fallbackCh, DateTime.UtcNow);
            return fallbackCh;
        }

        return null;
    }

    public async Task<List<YouTubeCommentDto>> GetVideoCommentsAsync(
        string videoId,
        string? usernameOrId,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var list = new List<YouTubeCommentDto>();
        var token = await EnsureUserAccessTokenAsync(usernameOrId, cancellationToken).ConfigureAwait(false)
                    ?? await EnsureUserAccessTokenAsync("fabian", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return list;
        }

        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/commentThreads?part=snippet&videoId={Uri.EscapeDataString(videoId)}&maxResults={Math.Min(50, maxResults)}&order=relevance&textFormat=plainText";
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
                        if (item.TryGetProperty("snippet", out var s) &&
                            s.TryGetProperty("topLevelComment", out var tlc) &&
                            tlc.TryGetProperty("snippet", out var cs))
                        {
                            var likes = cs.TryGetProperty("likeCount", out var lc) ? lc.GetInt32() : 0;
                            list.Add(new YouTubeCommentDto
                            {
                                Author = cs.TryGetProperty("authorDisplayName", out var a) ? a.GetString() ?? "User" : "User",
                                AuthorAvatarUrl = cs.TryGetProperty("authorProfileImageUrl", out var av) ? av.GetString() ?? string.Empty : string.Empty,
                                Text = cs.TryGetProperty("textDisplay", out var td) ? td.GetString() ?? string.Empty : string.Empty,
                                PublishedTimeText = cs.TryGetProperty("publishedAt", out var pa) ? pa.GetString() ?? string.Empty : string.Empty,
                                LikeCountText = likes > 0 ? $"👍 {likes}" : string.Empty
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch comments for {VideoId}", videoId);
        }

        return list;
    }

    public static string FormatRichOverview(YouTubeVideoItem video, ReturnYouTubeDislikeDto? ryd, List<SponsorBlockSegmentDto>? sb)
    {
        var sbText = new StringBuilder();
        var statsParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(video.ChannelTitle))
        {
            statsParts.Add($"👤 {video.ChannelTitle}");
        }

        if (ryd != null && (ryd.Likes > 0 || ryd.Dislikes > 0))
        {
            var total = Math.Max(1, ryd.Likes + ryd.Dislikes);
            var pct = (int)Math.Round((double)ryd.Likes / total * 100);
            statsParts.Add($"👍 {FormatCount(ryd.Likes)}  👎 {FormatCount(ryd.Dislikes)} ({pct}%)");
            if (ryd.ViewCount > 0)
            {
                statsParts.Add($"👁️ {FormatCount(ryd.ViewCount)} Views");
            }
        }
        else if (video.ViewCount > 0)
        {
            statsParts.Add($"👁️ {FormatCount(video.ViewCount)} Views");
        }

        if (!string.IsNullOrWhiteSpace(video.PublishedTimeText))
        {
            statsParts.Add($"📅 {video.PublishedTimeText}");
        }

        if (sb != null && sb.Count > 0)
        {
            var cats = string.Join(", ", sb.Select(s => s.Category).Distinct());
            statsParts.Add($"🛡️ SponsorBlock: {sb.Count} Segment(s) ({cats})");
        }

        if (statsParts.Count > 0)
        {
            sbText.AppendLine(string.Join("  •  ", statsParts));
            sbText.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(video.Description))
        {
            sbText.Append(video.Description);
        }

        return sbText.ToString().Trim();
    }

    public static string FormatCount(long n)
    {
        if (n >= 1_000_000)
        {
            return $"{n / 1_000_000.0:0.#}M";
        }

        if (n >= 1_000)
        {
            return $"{n / 1_000.0:0.#}K";
        }

        return n.ToString();
    }
}
