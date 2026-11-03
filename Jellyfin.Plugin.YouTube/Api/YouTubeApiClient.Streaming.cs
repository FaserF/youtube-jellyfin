using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient
{
    public async Task<YouTubeStreamInfoDto?> ResolveStreamAsync(
        string videoId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        if (_streamCache.TryGetValue(videoId, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalMinutes < 45)
        {
            return cached.Info;
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var ytDlpBin = !string.IsNullOrWhiteSpace(config.YtDlpPath) && File.Exists(config.YtDlpPath)
            ? config.YtDlpPath
            : "yt-dlp";

        try
        {
            var args = new List<string>
            {
                "--no-warnings",
                "--no-playlist",
                "-J"
            };

            if (!string.IsNullOrWhiteSpace(config.ProxyUrl))
            {
                args.Add("--proxy");
                args.Add(config.ProxyUrl);
            }

            if (!string.IsNullOrWhiteSpace(config.CookiesText))
            {
                var cookieFile = "/tmp/yt_jellyfin_cookies.txt";
                await File.WriteAllTextAsync(cookieFile, config.CookiesText, cancellationToken).ConfigureAwait(false);
                args.Add("--cookies");
                args.Add(cookieFile);
            }

            args.Add($"https://www.youtube.com/watch?v={videoId}");

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpBin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);

                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var root = doc.RootElement;

                    var info = new YouTubeStreamInfoDto
                    {
                        VideoId = videoId,
                        Title = root.TryGetProperty("title", out var t) ? t.GetString() ?? videoId : videoId,
                        ChannelTitle = root.TryGetProperty("uploader", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                        ChannelId = root.TryGetProperty("channel_id", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
                        Description = root.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
                        DurationSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? (long)dur.GetDouble() : 0
                    };

                    var maxHeight = config.PreferredMaxResolution > 0 ? config.PreferredMaxResolution : 2160;
                    string? bestHlsManifest = null;
                    string? bestCombinedUrl = null;
                    string? bestVideoOnlyUrl = null;
                    string? bestAudioOnlyUrl = null;
                    int bestHeight = 0;

                    if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in formats.EnumerateArray())
                        {
                            var url = f.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                            var manifest = f.TryGetProperty("manifest_url", out var mProp) ? mProp.GetString() : null;
                            var vcodec = f.TryGetProperty("vcodec", out var vcProp) ? vcProp.GetString() ?? "none" : "none";
                            var acodec = f.TryGetProperty("acodec", out var acProp) ? acProp.GetString() ?? "none" : "none";
                            var height = f.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number ? hProp.GetInt32() : 0;
                            var ext = f.TryGetProperty("ext", out var extProp) ? extProp.GetString() ?? string.Empty : string.Empty;

                            if (!string.IsNullOrWhiteSpace(manifest) && manifest.Contains("hls_variant", StringComparison.OrdinalIgnoreCase))
                            {
                                bestHlsManifest = manifest;
                            }

                            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                if (vcodec != "none" && acodec != "none" && height <= maxHeight && height >= bestHeight)
                                {
                                    bestCombinedUrl = url;
                                    bestHeight = height;
                                    info.Height = height;
                                    info.Width = f.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number ? wProp.GetInt32() : 1920;
                                }
                                else if (vcodec != "none" && acodec == "none" && height <= maxHeight && height >= bestHeight && ext == "mp4")
                                {
                                    bestVideoOnlyUrl = url;
                                    bestHeight = height;
                                    info.Height = height;
                                    info.Width = f.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number ? wProp.GetInt32() : 1920;
                                }
                                else if (vcodec == "none" && acodec != "none" && (ext == "m4a" || acodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)))
                                {
                                    bestAudioOnlyUrl = url;
                                }
                            }
                        }
                    }

                    info.HlsManifestUrl = bestHlsManifest ?? string.Empty;
                    info.DirectStreamUrl = bestCombinedUrl ?? bestHlsManifest ?? bestVideoOnlyUrl ?? string.Empty;
                    info.AudioStreamUrl = bestAudioOnlyUrl ?? string.Empty;

                    if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var langProp in subs.EnumerateObject())
                        {
                            if (langProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var subEntry in langProp.Value.EnumerateArray())
                                {
                                    var sExt = subEntry.TryGetProperty("ext", out var se) ? se.GetString() : null;
                                    var sUrl = subEntry.TryGetProperty("url", out var su) ? su.GetString() : null;
                                    if (sExt == "vtt" && !string.IsNullOrWhiteSpace(sUrl))
                                    {
                                        info.Subtitles[langProp.Name] = sUrl;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    var sbTask = GetSponsorBlockSegmentsAsync(videoId, cancellationToken);
                    var rydTask = GetReturnYouTubeDislikeAsync(videoId, cancellationToken);
                    await Task.WhenAll(sbTask, rydTask).ConfigureAwait(false);
                    info.SponsorSegments = await sbTask.ConfigureAwait(false);
                    info.DislikeStats = await rydTask.ConfigureAwait(false);

                    _streamCache[videoId] = (info, DateTime.UtcNow);
                    return info;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp stream resolution failed for {VideoId}", videoId);
        }

        return null;
    }

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
