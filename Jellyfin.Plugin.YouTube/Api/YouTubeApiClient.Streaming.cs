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
                    DurationSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? (long)dur.GetDouble() : 0,
                    NativeLanguage = root.TryGetProperty("language", out var langProp) ? langProp.GetString() : null,
                    Category = root.TryGetProperty("categories", out var catArr) && catArr.ValueKind == JsonValueKind.Array && catArr.GetArrayLength() > 0
                        ? catArr[0].GetString() ?? string.Empty : string.Empty,
                };

                // Parse tags
                if (root.TryGetProperty("tags", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsArr.EnumerateArray())
                    {
                        var tagStr = tag.GetString();
                        if (!string.IsNullOrWhiteSpace(tagStr))
                        {
                            info.Tags.Add(tagStr);
                        }
                    }
                }

                // Parse upload_date (yyyyMMdd format from yt-dlp)
                if (root.TryGetProperty("upload_date", out var uploadDateProp))
                {
                    var dateStr = uploadDateProp.GetString();
                    if (!string.IsNullOrWhiteSpace(dateStr) && dateStr.Length == 8 &&
                        int.TryParse(dateStr[..4], out var yr) &&
                        int.TryParse(dateStr[4..6], out var mo) &&
                        int.TryParse(dateStr[6..8], out var dy))
                    {
                        info.PublishedAt = new DateTime(yr, mo, dy, 0, 0, 0, DateTimeKind.Utc);
                    }
                }

                // Parse native YouTube chapters from uploader
                if (root.TryGetProperty("chapters", out var chaps) && chaps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in chaps.EnumerateArray())
                    {
                        var start = ch.TryGetProperty("start_time", out var stProp) && stProp.ValueKind == JsonValueKind.Number ? stProp.GetDouble() : 0;
                        var end = ch.TryGetProperty("end_time", out var etProp) && etProp.ValueKind == JsonValueKind.Number ? etProp.GetDouble() : 0;
                        var title = ch.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            info.Chapters.Add(new YouTubeChapterDto
                            {
                                Title = title,
                                StartSeconds = start,
                                EndSeconds = end
                            });
                        }
                    }
                }

                var maxHeight = config.PreferredMaxResolution > 0 ? config.PreferredMaxResolution : 2160;
                var prefLang = (config.PreferredLanguage ?? "de").Split('-')[0];
                string? bestHlsManifest = null;
                string? bestCombinedUrl = null;
                string? bestVideoOnlyUrl = null;
                string? bestAudioOnlyUrl = null;
                int bestAudioScore = -1;
                int bestHeight = 0;

                // Per-height best video-only format: height -> best (url, codec, bitrate, fps, width, formatId, hdr)
                var videoFormatMap = new Dictionary<int, (string Url, string Codec, long Bitrate, float Fps, int Width, string FormatId, bool IsHdr)>();

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
                        var width = f.TryGetProperty("width", out var wProp) && wProp.ValueKind == JsonValueKind.Number ? wProp.GetInt32() : 0;
                        var ext = f.TryGetProperty("ext", out var extProp) ? extProp.GetString() ?? string.Empty : string.Empty;
                        var lang = f.TryGetProperty("language", out var lProp) ? lProp.GetString() : null;
                        var formatNote = f.TryGetProperty("format_note", out var fnProp) ? fnProp.GetString() ?? string.Empty : string.Empty;
                        var tbr = f.TryGetProperty("tbr", out var tbrProp) && tbrProp.ValueKind == JsonValueKind.Number ? (long)(tbrProp.GetDouble() * 1000) : 128000;
                        var abr = f.TryGetProperty("abr", out var abrProp) && abrProp.ValueKind == JsonValueKind.Number ? (int)(abrProp.GetDouble() * 1000) : (int)tbr;
                        var vbr = f.TryGetProperty("vbr", out var vbrProp) && vbrProp.ValueKind == JsonValueKind.Number ? (long)(vbrProp.GetDouble() * 1000) : tbr;
                        var fps = f.TryGetProperty("fps", out var fpsProp) && fpsProp.ValueKind == JsonValueKind.Number ? (float)fpsProp.GetDouble() : 30f;
                        var sampleRate = f.TryGetProperty("asr", out var asrProp) && asrProp.ValueKind == JsonValueKind.Number ? asrProp.GetInt32() : 44100;
                        var channels = f.TryGetProperty("audio_channels", out var chProp) && chProp.ValueKind == JsonValueKind.Number ? chProp.GetInt32() : 2;
                        var isHdr = formatNote.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                                    vcodec.Contains("vp09.2", StringComparison.OrdinalIgnoreCase);

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
                                info.Width = width > 0 ? width : 1920;
                            }
                            else if (vcodec != "none" && acodec == "none" && height > 0 && height <= maxHeight)
                            {
                                // Collect all video-only formats per height for multi-resolution
                                var isPreferredCodec = ext == "mp4" ||
                                                       vcodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
                                                       vcodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase);
                                if (isPreferredCodec)
                                {
                                    var existingEntry = videoFormatMap.TryGetValue(height, out var existing);
                                    var preferNew = !existingEntry ||
                                                    (ext == "mp4" && !existing.Codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) ||
                                                    (ext == existing.Codec && vbr > existing.Bitrate);
                                    if (preferNew)
                                    {
                                        videoFormatMap[height] = (url, ext == "mp4" ? "h264" : "vp9", vbr, fps, width > 0 ? width : height * 16 / 9, formatId, isHdr);
                                    }
                                }

                                if (height <= maxHeight && height >= bestHeight)
                                {
                                    bestVideoOnlyUrl = url;
                                    bestHeight = height;
                                    info.Height = height;
                                    info.Width = width > 0 ? width : 1920;
                                }
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

                    // Build multi-resolution VideoFormats list (C: all available resolutions up to 4K)
                    info.VideoFormats = videoFormatMap
                        .OrderByDescending(kv => kv.Key)
                        .Select(kv =>
                        {
                            var h = kv.Key;
                            var (vUrl, codec, bitrate, fps2, w, fid, hdr) = kv.Value;
                            var label = h switch
                            {
                                2160 => hdr ? "4K UHD HDR" : "4K UHD",
                                1440 => hdr ? "1440p QHD HDR" : "1440p QHD",
                                1080 => fps2 >= 50 ? "1080p60 Full HD" : (hdr ? "1080p Full HD HDR" : "1080p Full HD"),
                                720  => fps2 >= 50 ? "720p60 HD" : "720p HD",
                                480  => "480p SD",
                                360  => "360p SD",
                                240  => "240p LD",
                                144  => "144p LD",
                                _    => $"{h}p"
                            };
                            return new YouTubeVideoFormatDto
                            {
                                Height = h,
                                Width = w,
                                Codec = codec,
                                Bitrate = bitrate,
                                Fps = fps2,
                                IsHdr = hdr,
                                StreamUrl = vUrl,
                                FormatId = fid,
                                Label = label
                            };
                        })
                        .ToList();
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
        var config = Plugin.Instance?.Configuration;
        var prefLang = (config?.PreferredLanguage ?? "de").Split('-')[0];

        // 1. Official/Manual Subtitles — always include all
        var manualLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
        {
            foreach (var langProp in subs.EnumerateObject())
            {
                if (langProp.Value.ValueKind != JsonValueKind.Array) continue;
                // Skip internal yt metadata keys
                if (langProp.Name.Equals("live_chat", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var entry in langProp.Value.EnumerateArray())
                {
                    var ext = entry.TryGetProperty("ext", out var eProp) ? eProp.GetString() : null;
                    var url = entry.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    var name = entry.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(url) && (ext == "vtt" || ext == "ttml" || ext == "srv3" || ext == "srv1"))
                    {
                        var langCode = langProp.Name;
                        var baseLang = langCode.Split('-')[0];
                        var title = !string.IsNullOrWhiteSpace(name) ? name : langCode;
                        var isDefault = baseLang.Equals(prefLang, StringComparison.OrdinalIgnoreCase) ||
                                        baseLang.Equals("en", StringComparison.OrdinalIgnoreCase);
                        info.Subtitles[langCode] = url;
                        manualLangs.Add(langCode);
                        manualLangs.Add(baseLang);
                        subtitleTrackMap[langCode] = new YouTubeSubtitleTrackDto
                        {
                            Language = langCode,
                            Title = title,
                            Format = "vtt",
                            IsAutoGenerated = false,
                            IsDefault = isDefault,
                            StreamUrl = url
                        };
                        break;
                    }
                }
            }
        }

        // 2. Auto-Generated Captions — ONLY for the video's native language & preferred language.
        // YouTube auto-generates captions in the native language and also offers auto-TRANSLATIONS
        // into 100+ languages. We detect native auto-captions vs. translations:
        // - Native: langCode matches info.NativeLanguage or is the only non-prefixed code
        // - Translation: entries in automatic_captions that are NOT the native language are translations
        // Rule: include only native-language auto-captions + preferred-language auto-captions (if available).
        // This matches SmartTube behaviour: shows the actual subtitle options, not 100+ translation stubs.
        if (root.TryGetProperty("automatic_captions", out var autoSubs) && autoSubs.ValueKind == JsonValueKind.Object)
        {
            var nativeLang = info.NativeLanguage?.Split('-')[0] ?? string.Empty;

            // Build set of languages to include from auto-captions
            var allowedAutoCaptionLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Always allow native language
            if (!string.IsNullOrWhiteSpace(nativeLang))
            {
                allowedAutoCaptionLangs.Add(nativeLang);
            }

            // Allow preferred language
            allowedAutoCaptionLangs.Add(prefLang);
            allowedAutoCaptionLangs.Add("de");
            allowedAutoCaptionLangs.Add("en");

            // Also scan for any lang codes in auto_captions that DON'T have a "-orig" suffix
            // and appear to be real captions (not machine translations of other languages).
            // Key insight: yt-dlp marks translated auto-captions with the source lang in the URL
            // e.g. tlang=fr means it's a translated caption. We detect these by checking if
            // the URL contains "tlang=" which indicates it's a translation endpoint.
            foreach (var langProp in autoSubs.EnumerateObject())
            {
                var langCode = langProp.Name;
                var baseLang = langCode.Split('-')[0];
                if (subtitleTrackMap.ContainsKey(langCode)) continue;
                if (langProp.Value.ValueKind != JsonValueKind.Array) continue;

                // Check if this is a native auto-caption or a translation
                var isAllowed = allowedAutoCaptionLangs.Contains(baseLang) || allowedAutoCaptionLangs.Contains(langCode);
                if (!isAllowed) continue;

                foreach (var entry in langProp.Value.EnumerateArray())
                {
                    var ext = entry.TryGetProperty("ext", out var eProp) ? eProp.GetString() : null;
                    var url = entry.TryGetProperty("url", out var uProp) ? uProp.GetString() : null;
                    var name = entry.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(url) && (ext == "vtt" || ext == "ttml" || ext == "srv3" || ext == "srv1"))
                    {
                        // Skip if it's a translated caption (URL contains tlang= pointing to another language)
                        var isTranslation = !string.IsNullOrWhiteSpace(nativeLang) &&
                                            url.Contains("tlang=", StringComparison.OrdinalIgnoreCase) &&
                                            !url.Contains($"tlang={Uri.EscapeDataString(baseLang)}", StringComparison.OrdinalIgnoreCase) &&
                                            !url.Contains($"tlang={baseLang}", StringComparison.OrdinalIgnoreCase);
                        if (isTranslation) continue;

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
}
