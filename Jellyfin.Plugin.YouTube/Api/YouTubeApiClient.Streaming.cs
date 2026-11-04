using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, Task<YouTubeStreamInfoDto?>> _inflightStreams = new(StringComparer.OrdinalIgnoreCase);

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

        var task = _inflightStreams.GetOrAdd(videoId, id => ResolveStreamCoreAsync(id));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                _inflightStreams.TryRemove(videoId, out _);
            }
        }
    }

    private async Task<YouTubeStreamInfoDto?> ResolveStreamCoreAsync(string videoId)
    {
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
            var stdout = await RunYtDlpJsonAsync(ytDlpBin, videoId, config, useProxy: true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(config.ProxyUrl))
            {
                _logger.LogInformation("Retrying yt-dlp without proxy for {VideoId}", videoId);
                stdout = await RunYtDlpJsonAsync(ytDlpBin, videoId, config, useProxy: false).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(stdout))
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
                var prefLang = (config.PreferredLanguage ?? "de").Split('-')[0];
                string? bestHlsManifest = null;
                string? bestCombinedUrl = null;
                string? bestVideoOnlyUrl = null;
                string? bestAudioOnlyUrl = null;
                int bestAudioScore = -1;
                int bestHeight = 0;

                if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
                {
                    var audioTrackMap = new Dictionary<string, YouTubeAudioTrackDto>(StringComparer.OrdinalIgnoreCase);

                    foreach (var f in formats.EnumerateArray())
                    {
                        var formatId = f.TryGetProperty("format_id", out var fidProp) ? fidProp.GetString() ?? string.Empty : string.Empty;
                        var url = f.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                        var manifest = f.TryGetProperty("manifest_url", out var mProp) ? mProp.GetString() : null;
                        var vcodec = f.TryGetProperty("vcodec", out var vcProp) ? vcProp.GetString() ?? "none" : "none";
                        var acodec = f.TryGetProperty("acodec", out var acProp) ? acProp.GetString() ?? "none" : "none";
                        var height = f.TryGetProperty("height", out var hProp) && hProp.ValueKind == JsonValueKind.Number ? hProp.GetInt32() : 0;
                        var ext = f.TryGetProperty("ext", out var extProp) ? extProp.GetString() ?? string.Empty : string.Empty;
                        var lang = f.TryGetProperty("language", out var lProp) ? lProp.GetString() : null;
                        var formatNote = f.TryGetProperty("format_note", out var fnProp) ? fnProp.GetString() ?? string.Empty : string.Empty;
                        var tbr = f.TryGetProperty("tbr", out var tbrProp) && tbrProp.ValueKind == JsonValueKind.Number ? (int)(tbrProp.GetDouble() * 1000) : 128000;
                        var abr = f.TryGetProperty("abr", out var abrProp) && abrProp.ValueKind == JsonValueKind.Number ? (int)(abrProp.GetDouble() * 1000) : tbr;
                        var sampleRate = f.TryGetProperty("asr", out var asrProp) && asrProp.ValueKind == JsonValueKind.Number ? asrProp.GetInt32() : 44100;
                        var channels = f.TryGetProperty("audio_channels", out var chProp) && chProp.ValueKind == JsonValueKind.Number ? chProp.GetInt32() : 2;

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
                            else if (vcodec == "none" && acodec != "none" && !IsM3u8Url(url))
                            {
                                var isAac = ext.Equals("m4a", StringComparison.OrdinalIgnoreCase) || acodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase);
                                var langKey = !string.IsNullOrWhiteSpace(lang) ? lang : "und";
                                var isOriginal = formatNote.Contains("original", StringComparison.OrdinalIgnoreCase) || formatNote.Contains("default", StringComparison.OrdinalIgnoreCase);
                                var isDub = formatNote.Contains("dubbed", StringComparison.OrdinalIgnoreCase) || formatNote.Contains("dub", StringComparison.OrdinalIgnoreCase);
                                var isPrefLang = langKey.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase);

                                var audioScore = (isAac ? 1_000_000 : 0) +
                                                 (isPrefLang ? 500_000 : (!isDub ? 300_000 : 0)) +
                                                 (isOriginal ? 100_000 : 0) +
                                                 Math.Min(abr, 500_000);
                                if (audioScore > bestAudioScore)
                                {
                                    bestAudioScore = audioScore;
                                    bestAudioOnlyUrl = url;
                                }

                                var trackTitle = !string.IsNullOrWhiteSpace(formatNote)
                                    ? $"{formatNote} ({langKey})"
                                    : (langKey != "und" ? langKey : "Audio");

                                var trackKey = $"{langKey}_{isDub}";
                                var existingIsAac = audioTrackMap.TryGetValue(trackKey, out var existingTrack) && existingTrack.Codec == "aac";
                                if (existingTrack == null || (isAac && !existingIsAac) || (isAac == existingIsAac && abr > existingTrack.Bitrate))
                                {
                                    audioTrackMap[trackKey] = new YouTubeAudioTrackDto
                                    {
                                        FormatId = formatId,
                                        Language = langKey,
                                        Title = trackTitle,
                                        Codec = isAac ? "aac" : (acodec.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "opus" : "aac"),
                                        Bitrate = abr > 0 ? abr : 128000,
                                        Channels = channels,
                                        SampleRate = sampleRate,
                                        IsOriginal = isOriginal,
                                        IsDubbed = isDub,
                                        IsDefault = false,
                                        StreamUrl = url
                                    };
                                }
                            }
                        }
                    }

                    var tracks = audioTrackMap.Values
                        .OrderByDescending(a => a.Language.StartsWith(prefLang, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(a => a.IsOriginal || !a.IsDubbed)
                        .ThenByDescending(a => a.Codec == "aac")
                        .ThenByDescending(a => a.Bitrate)
                        .ToList();
                    if (tracks.Count > 0)
                    {
                        tracks[0].IsDefault = true;
                    }

                    info.AudioTracks = tracks;
                }

                info.HlsManifestUrl = bestHlsManifest ?? string.Empty;
                info.DirectStreamUrl = bestCombinedUrl ?? bestHlsManifest ?? bestVideoOnlyUrl ?? bestAudioOnlyUrl ?? string.Empty;
                info.AudioStreamUrl = bestAudioOnlyUrl ?? string.Empty;

                ParseSubtitlesAndCaptions(root, info);

                try
                {
                    using var metaCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    var sbTask = GetSponsorBlockSegmentsAsync(videoId, metaCts.Token);
                    var rydTask = GetReturnYouTubeDislikeAsync(videoId, metaCts.Token);
                    await Task.WhenAll(sbTask, rydTask).ConfigureAwait(false);
                    info.SponsorSegments = await sbTask.ConfigureAwait(false);
                    info.DislikeStats = await rydTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Timed out or failed fetching optional SponsorBlock/RYD metadata during stream resolve for {VideoId}", videoId);
                }

                _streamCache[videoId] = (info, DateTime.UtcNow);
                return info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "yt-dlp stream resolution failed for {VideoId}", videoId);
        }

        return null;
    }

    private static bool IsM3u8Url(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("hls_playlist", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("hls_variant", StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> RunYtDlpJsonAsync(
        string ytDlpBin,
        string videoId,
        PluginConfiguration config,
        bool useProxy)
    {
        var args = new List<string>
        {
            "--no-warnings",
            "--no-playlist",
            "--no-check-certificates",
            "--geo-bypass",
            "--geo-bypass-country",
            !string.IsNullOrWhiteSpace(config.PreferredRegion) ? config.PreferredRegion : "DE",
            "--socket-timeout",
            "10",
            "--no-write-auto-subs",
            "--no-write-subs",
            "--skip-download",
            "-J"
        };

        var denoPath = File.Exists("/usr/local/bin/deno")
            ? "/usr/local/bin/deno"
            : (File.Exists("/config/bin/deno") ? "/config/bin/deno" : null);
        if (denoPath != null)
        {
            args.Add("--js-runtimes");
            args.Add($"deno:{denoPath}");
        }

        if (useProxy && !string.IsNullOrWhiteSpace(config.ProxyUrl))
        {
            args.Add("--proxy");
            args.Add(config.ProxyUrl);
        }

        if (!string.IsNullOrWhiteSpace(config.CookiesText))
        {
            var cookieFile = "/tmp/yt_jellyfin_cookies.txt";
            await File.WriteAllTextAsync(cookieFile, config.CookiesText).ConfigureAwait(false);
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return null;
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);

        return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : null;
    }

    private static void ParseSubtitlesAndCaptions(JsonElement root, YouTubeStreamInfoDto info)
    {
        var subtitleTrackMap = new Dictionary<string, YouTubeSubtitleTrackDto>(StringComparer.OrdinalIgnoreCase);

        // 1. Official/Manual Subtitles
        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
        {
            foreach (var langProp in subs.EnumerateObject())
            {
                if (langProp.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in langProp.Value.EnumerateArray())
                {
                    var ext = entry.TryGetProperty("ext", out var eProp) ? eProp.GetString() : null;
                    var url = entry.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    var name = entry.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(url) && (ext == "vtt" || ext == "ttml" || ext == "srv3"))
                    {
                        var langCode = langProp.Name;
                        var title = !string.IsNullOrWhiteSpace(name) ? name : langCode;
                        info.Subtitles[langCode] = url;
                        subtitleTrackMap[langCode] = new YouTubeSubtitleTrackDto
                        {
                            Language = langCode,
                            Title = title,
                            Format = "vtt",
                            IsAutoGenerated = false,
                            IsDefault = langCode.StartsWith("de", StringComparison.OrdinalIgnoreCase) || langCode.StartsWith("en", StringComparison.OrdinalIgnoreCase),
                            StreamUrl = url
                        };
                        break;
                    }
                }
            }
        }

        // 2. Automatic Captions (SmartTube 1:1 auto-caption parity)
        if (root.TryGetProperty("automatic_captions", out var autoSubs) && autoSubs.ValueKind == JsonValueKind.Object)
        {
            foreach (var langProp in autoSubs.EnumerateObject())
            {
                var langCode = langProp.Name;
                if (subtitleTrackMap.ContainsKey(langCode)) continue;
                if (langProp.Value.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in langProp.Value.EnumerateArray())
                {
                    var ext = entry.TryGetProperty("ext", out var eProp) ? eProp.GetString() : null;
                    var url = entry.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    var name = entry.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(url) && (ext == "vtt" || ext == "ttml" || ext == "srv3"))
                    {
                        var title = !string.IsNullOrWhiteSpace(name)
                            ? $"{name} (Auto)"
                            : $"{langCode} (Auto)";

                        if (!info.Subtitles.ContainsKey(langCode))
                        {
                            info.Subtitles[langCode] = url;
                        }

                        subtitleTrackMap[langCode] = new YouTubeSubtitleTrackDto
                        {
                            Language = langCode,
                            Title = title,
                            Format = "vtt",
                            IsAutoGenerated = true,
                            IsDefault = false,
                            StreamUrl = url
                        };
                        break;
                    }
                }
            }
        }

        info.SubtitleTracks = subtitleTrackMap.Values
            .OrderBy(s => s.IsAutoGenerated)
            .ThenByDescending(s => s.IsDefault)
            .ThenBy(s => s.Language)
            .ToList();
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
}
