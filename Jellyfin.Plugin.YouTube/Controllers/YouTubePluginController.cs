using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Controllers;

[ApiController]
[Route("api/youtubePlugin")]
public class YouTubePluginController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<YouTubePluginController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubePluginController(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<YouTubePluginController> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    [HttpGet("Script")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("youtubePlugin.js", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            return NotFound("// youtubePlugin.js resource not found");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound("// youtubePlugin.js stream null");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    [HttpPost("auth/device-code")]
    [AllowAnonymous]
    public async Task<ActionResult<YouTubeDeviceCodeResponse>> RequestDeviceCode(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var resp = await client.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        if (resp == null)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to request YouTube TV device code" });
        }

        return Ok(resp);
    }

    [HttpPost("auth/poll")]
    [AllowAnonymous]
    public async Task<ActionResult> PollDeviceCode(
        [FromQuery] string deviceCode,
        [FromQuery] string jellyfinUser = "fabian",
        [FromQuery] string? jellyfinUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest(new { error = "Missing deviceCode" });
        }

        using var client = CreateClient();
        var (success, error, account) = await client.PollDeviceTokenAsync(deviceCode, jellyfinUser, jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            return Ok(new { pending = true, error });
        }

        return Ok(new
        {
            pending = false,
            success = true,
            jellyfinUser = account?.JellyfinUser,
            accountName = account?.AccountName
        });
    }

    [HttpGet("users")]
    [AllowAnonymous]
    public async Task<ActionResult> GetUserMappings(CancellationToken cancellationToken)
    {
        var config = Config;
        var jfUsers = new List<object>();
        try
        {
            using var http = _httpClientFactory.CreateClient();
            var json = await http.GetStringAsync("http://127.0.0.1:8096/Users/Public", cancellationToken).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var uid = el.TryGetProperty("Id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                    var uname = el.TryGetProperty("Name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrWhiteSpace(uname))
                    {
                        var linkedAcc = config.GetAccountForUser(uname)?.AccountName ?? config.GetAccountForUser(uid)?.AccountName;
                        jfUsers.Add(new
                        {
                            id = uid,
                            username = uname,
                            linkedAccount = linkedAcc,
                            isLinked = !string.IsNullOrWhiteSpace(linkedAcc)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query /Users/Public");
        }

        return Ok(new
        {
            enablePublicFallback = config.EnablePublicFallbackForUnlinkedUsers,
            primaryUser = config.PrimaryJellyfinUser,
            users = jfUsers,
            accounts = (config.UserAccounts ?? new List<YouTubeUserAccountEntry>()).Select(a => new
            {
                a.JellyfinUser,
                a.JellyfinUserId,
                a.AccountName,
                hasRefreshToken = !string.IsNullOrWhiteSpace(a.RefreshToken)
            })
        });
    }

    [HttpDelete("users/{jellyfinUser}")]
    [AllowAnonymous]
    public ActionResult UnlinkUserAccount([FromRoute] string jellyfinUser)
    {
        var config = Config;
        if (config.UserAccounts != null)
        {
            config.UserAccounts.RemoveAll(u =>
                string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUser), PluginConfiguration.NormalizeId(jellyfinUser), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUserId), PluginConfiguration.NormalizeId(jellyfinUser), StringComparison.OrdinalIgnoreCase));
        }

        if (config.IsPrimaryUser(jellyfinUser))
        {
            config.PersistedRefreshToken = string.Empty;
            config.PersistedAccessToken = string.Empty;
        }

        Plugin.Instance?.SaveConfiguration();
        return Ok(new { unlinked = true, jellyfinUser });
    }

    [HttpGet("stream/{videoId}/info")]
    [AllowAnonymous]
    public async Task<ActionResult<YouTubeStreamInfoDto>> GetStreamInfo(
        [FromRoute] string videoId,
        CancellationToken cancellationToken)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        if (info == null)
        {
            return NotFound(new { error = $"Could not resolve YouTube stream for {cleanId}" });
        }

        return Ok(info);
    }

    [HttpGet("stream/{videoId}/play")]
    [HttpGet("stream/{videoId}/master.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> PlayOrProxyStream(
        [FromRoute] string videoId,
        CancellationToken cancellationToken)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        if (info == null || string.IsNullOrWhiteSpace(info.DirectStreamUrl))
        {
            return NotFound(new { error = $"Unable to resolve YouTube stream for {cleanId}" });
        }

        // If we have an HLS manifest URL, fetch it through our SOCKS5 proxy and rewrite segment/variant URLs
        // so that IP-bound googlevideo URLs are proxied cleanly through Jellyfin!
        if (!string.IsNullOrWhiteSpace(info.HlsManifestUrl))
        {
            try
            {
                var rawM3u8 = await client.HttpClient.GetStringAsync(info.HlsManifestUrl, cancellationToken).ConfigureAwait(false);
                var rewritten = RewriteM3u8Urls(rawM3u8, info.HlsManifestUrl);
                return Content(rewritten, "application/vnd.apple.mpegurl; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to proxy HLS manifest for {VideoId}, falling back to direct stream proxy", cleanId);
            }
        }

        return await ProxyRemoteUrlInternalAsync(client, info.DirectStreamUrl, cancellationToken).ConfigureAwait(false);
    }

    [HttpGet("stream/{videoId}/audio.m4a")]
    [HttpGet("stream/{videoId}/audio")]
    [HttpGet("stream/{videoId}/audio-{formatId}.m4a")]
    [AllowAnonymous]
    public async Task<ActionResult> PlayOrProxyAudioStream(
        [FromRoute] string videoId,
        [FromRoute] string? formatId = null,
        [FromQuery] string? lang = null,
        CancellationToken cancellationToken = default)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        if (info == null)
        {
            return NotFound(new { error = $"Unable to resolve YouTube Music audio stream for {cleanId}" });
        }

        string? audioUrl = null;

        // 1. If formatId specified, find that exact track
        if (!string.IsNullOrWhiteSpace(formatId) && info.AudioTracks.Count > 0)
        {
            audioUrl = info.AudioTracks.FirstOrDefault(a => string.Equals(a.FormatId, formatId, StringComparison.OrdinalIgnoreCase))?.StreamUrl;
        }

        // 2. If lang specified, find matching audio dub track
        if (string.IsNullOrWhiteSpace(audioUrl) && !string.IsNullOrWhiteSpace(lang) && info.AudioTracks.Count > 0)
        {
            audioUrl = info.AudioTracks.FirstOrDefault(a => a.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase))?.StreamUrl;
        }

        // 3. Fallback to default audio track or best audio stream URL
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            audioUrl = info.AudioTracks.FirstOrDefault(a => a.IsDefault)?.StreamUrl
                       ?? (!string.IsNullOrWhiteSpace(info.AudioStreamUrl) ? info.AudioStreamUrl : info.DirectStreamUrl);
        }

        if (!string.IsNullOrWhiteSpace(audioUrl) && !IsM3u8PlaylistUrl(audioUrl))
        {
            return await ProxyRemoteUrlInternalAsync(client, audioUrl, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(info.HlsManifestUrl))
        {
            var rawM3u8 = await client.HttpClient.GetStringAsync(info.HlsManifestUrl, cancellationToken).ConfigureAwait(false);
            var rewritten = RewriteM3u8Urls(rawM3u8, info.HlsManifestUrl);
            return Content(rewritten, "application/vnd.apple.mpegurl; charset=utf-8");
        }

        return NotFound();
    }

    [HttpGet("stream/{videoId}/subtitles/{lang}.vtt")]
    [AllowAnonymous]
    [Produces("text/vtt")]
    public async Task<ActionResult> GetSubtitleVtt(
        [FromRoute] string videoId,
        [FromRoute] string lang,
        CancellationToken cancellationToken = default)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        if (info == null || (info.Subtitles.Count == 0 && info.SubtitleTracks.Count == 0))
        {
            return Content("WEBVTT\n\n", "text/vtt; charset=utf-8");
        }

        string? subUrl = null;

        // 1. Check SubtitleTracks list first
        var matchTrack = info.SubtitleTracks.FirstOrDefault(s => string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase))
                         ?? info.SubtitleTracks.FirstOrDefault(s => s.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
        if (matchTrack != null)
        {
            subUrl = matchTrack.StreamUrl;
        }

        // 2. Fallback to Subtitles dictionary
        if (string.IsNullOrWhiteSpace(subUrl))
        {
            if (!info.Subtitles.TryGetValue(lang, out subUrl))
            {
                subUrl = info.Subtitles.FirstOrDefault(k => k.Key.StartsWith(lang, StringComparison.OrdinalIgnoreCase)).Value
                         ?? info.Subtitles.Values.FirstOrDefault();
            }
        }

        if (string.IsNullOrWhiteSpace(subUrl))
        {
            return Content("WEBVTT\n\n", "text/vtt; charset=utf-8");
        }

        try
        {
            // Ensure fmt=vtt parameter is present for YouTube timedtext API endpoints
            if (subUrl.Contains("/api/timedtext", StringComparison.OrdinalIgnoreCase) && !subUrl.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase))
            {
                subUrl += (subUrl.Contains('?') ? "&" : "?") + "fmt=vtt";
            }

            var rawSub = await client.HttpClient.GetStringAsync(subUrl, cancellationToken).ConfigureAwait(false);
            if (!rawSub.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            {
                rawSub = "WEBVTT\n\n" + rawSub;
            }

            return Content(rawSub, "text/vtt; charset=utf-8");
        }
        catch
        {
            return Content("WEBVTT\n\n", "text/vtt; charset=utf-8");
        }
    }

    [HttpGet("proxy")]
    [HttpGet("proxy/{filename}")]
    [AllowAnonymous]
    public async Task<ActionResult> ProxyUrl(
        [FromRoute] string? filename = null,
        [FromQuery] string? u = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(u))
        {
            return BadRequest();
        }

        string targetUrl;
        try
        {
            targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(u));
        }
        catch
        {
            targetUrl = Uri.UnescapeDataString(u);
        }

        if (!targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        using var client = CreateClient();
        if (IsM3u8PlaylistUrl(targetUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            using var resp = await client.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return await ProxyRemoteUrlInternalAsync(client, targetUrl, cancellationToken).ConfigureAwait(false);
            }

            var rawM3u8 = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rewritten = RewriteM3u8Urls(rawM3u8, targetUrl);
            return Content(rewritten, "application/vnd.apple.mpegurl; charset=utf-8");
        }

        return await ProxyRemoteUrlInternalAsync(client, targetUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult> ProxyRemoteUrlInternalAsync(YouTubeApiClient client, string targetUrl, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        if (Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            req.Headers.TryAddWithoutValidation("Range", rangeHeader.ToArray());
        }

        using var resp = await client.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Response.StatusCode = (int)resp.StatusCode;

        if (resp.Content.Headers.ContentRange != null)
        {
            Response.Headers["Content-Range"] = resp.Content.Headers.ContentRange.ToString();
        }

        if (resp.Content.Headers.ContentLength.HasValue)
        {
            Response.ContentLength = resp.Content.Headers.ContentLength.Value;
        }

        Response.Headers["Accept-Ranges"] = "bytes";
        var isVtt = targetUrl.Contains("timedtext", StringComparison.OrdinalIgnoreCase) ||
                    targetUrl.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase);
        var isAudioM4a = targetUrl.Contains("mime=audio%2Fmp4", StringComparison.OrdinalIgnoreCase) ||
                         targetUrl.Contains("mime=audio/mp4", StringComparison.OrdinalIgnoreCase);
        Response.ContentType = isVtt
            ? "text/vtt; charset=utf-8"
            : isAudioM4a
                ? "audio/mp4"
                : (resp.Content.Headers.ContentType?.ToString() ?? "video/mp2t");
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await stream.CopyToAsync(Response.Body, 65536, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    private static bool IsM3u8PlaylistUrl(string url) =>
        !url.Contains("seg.ts", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/videoplayback/", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("/api/timedtext", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase) &&
        (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("hls_playlist", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("hls_variant", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("timedtext_playlist", StringComparison.OrdinalIgnoreCase));

    private static string GetProxyExtensionForUrl(string url)
    {
        if (IsM3u8PlaylistUrl(url))
        {
            return "index.m3u8";
        }

        if (url.Contains("/api/timedtext", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
        {
            return "sub.vtt";
        }

        return "seg.ts";
    }

    private string RewriteM3u8Urls(string m3u8Content, string baseUrl)
    {
        var baseUri = new Uri(baseUrl);
        var sb = new StringBuilder();
        var lines = m3u8Content.Split('\n');
        var serverUrl = Config.GetPublicServerUrl(Request.Scheme, Request.Host.Value);
        var prefLang = Config.PreferredLanguage ?? "de";

        // Keep track of audio groups and whether a default audio track was chosen per group
        var defaultAudioSetForGroup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-scan to identify audio languages present: if preferred language exists, keep it + original (en).
        // Otherwise keep original + first fallback audio tracks.
        var hasPrefAudio = m3u8Content.Contains($"LANGUAGE=\"{prefLang}", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
                continue;
            }

            if (line.StartsWith('#'))
            {
                // 1. Strip HLS SUBTITLES tracks from master manifest so FFmpeg remux (-codec copy -map -0:s)
                // never attempts to open WebVTT subtitle playlists as MPEG-TS inputs
                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    line = System.Text.RegularExpressions.Regex.Replace(line, @",SUBTITLES=""[^""]*""", string.Empty);
                }

                // 2. Filter #EXT-X-MEDIA:TYPE=AUDIO:
                // Only keep preferred language track (e.g. de / de-DE) and original track (e.g. en / original).
                // Drop 30+ foreign audio dub variants from master manifest so client probing and FFmpeg remux start instantly.
                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    var isPrefLang = line.Contains($"LANGUAGE=\"{prefLang}", StringComparison.OrdinalIgnoreCase);
                    var isOriginal = line.Contains("original", StringComparison.OrdinalIgnoreCase) ||
                                     line.Contains("LANGUAGE=\"en", StringComparison.OrdinalIgnoreCase);

                    // If video has preferred language, keep prefLang and original; drop other dubs
                    // If video doesn't have preferred language, keep original and default
                    var shouldKeep = isPrefLang || isOriginal;
                    if (!hasPrefAudio && !shouldKeep && line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                    {
                        shouldKeep = true;
                    }

                    if (!shouldKeep)
                    {
                        continue;
                    }

                    // Extract GROUP-ID
                    var groupMatch = System.Text.RegularExpressions.Regex.Match(line, @"GROUP-ID=""([^""]+)""");
                    var groupId = groupMatch.Success ? groupMatch.Groups[1].Value : "default";

                    if (!defaultAudioSetForGroup.Contains(groupId))
                    {
                        if (isPrefLang || (!hasPrefAudio && isOriginal))
                        {
                            if (line.Contains("DEFAULT=NO", StringComparison.OrdinalIgnoreCase))
                            {
                                line = line.Replace("DEFAULT=NO", "DEFAULT=YES", StringComparison.OrdinalIgnoreCase);
                            }
                            else if (!line.Contains("DEFAULT=", StringComparison.OrdinalIgnoreCase))
                            {
                                line += ",DEFAULT=YES";
                            }
                            defaultAudioSetForGroup.Add(groupId);
                        }
                    }
                    else
                    {
                        // Default already set for this group, ensure this secondary track is DEFAULT=NO
                        if (line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                        {
                            line = line.Replace("DEFAULT=YES", "DEFAULT=NO", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                // Rewrite URI="..." inside #EXT-X-MEDIA or #EXT-X-MAP tags
                var uriUpdated = System.Text.RegularExpressions.Regex.Replace(line, @"URI=""([^""]+)""", m =>
                {
                    var abs = new Uri(baseUri, m.Groups[1].Value).ToString();
                    var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs));
                    var ext = GetProxyExtensionForUrl(abs);
                    return $"URI=\"{serverUrl}/api/youtubePlugin/proxy/{ext}?u={Uri.EscapeDataString(b64)}\"";
                });
                sb.AppendLine(uriUpdated);
            }
            else
            {
                var abs = new Uri(baseUri, line).ToString();
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(abs));
                var ext = GetProxyExtensionForUrl(abs);
                sb.AppendLine($"{serverUrl}/api/youtubePlugin/proxy/{ext}?u={Uri.EscapeDataString(b64)}");
            }
        }

        return sb.ToString();
    }

    [HttpGet("sponsorblock/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<List<SponsorBlockSegmentDto>>> GetSponsorBlock(
        [FromRoute] string videoId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var segments = await client.GetSponsorBlockSegmentsAsync(CleanVideoId(videoId), cancellationToken).ConfigureAwait(false);
        return Ok(segments);
    }

    [HttpGet("ryd/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<ReturnYouTubeDislikeDto>> GetDislikes(
        [FromRoute] string videoId,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var stats = await client.GetReturnYouTubeDislikeAsync(CleanVideoId(videoId), cancellationToken).ConfigureAwait(false);
        return Ok(stats ?? new ReturnYouTubeDislikeDto { Id = CleanVideoId(videoId) });
    }

    [HttpGet("comments/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<List<YouTubeCommentDto>>> GetComments(
        [FromRoute] string videoId,
        [FromQuery] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var comments = await client.GetVideoCommentsAsync(CleanVideoId(videoId), userId, 30, cancellationToken).ConfigureAwait(false);
        return Ok(comments);
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<ActionResult<List<YouTubeVideoItem>>> Search(
        [FromQuery] string q,
        [FromQuery] string? userId = null,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(new List<YouTubeVideoItem>());
        }

        using var client = CreateClient();
        var results = await client.SearchVideosAsync(q, userId, limit, cancellationToken).ConfigureAwait(false);
        return Ok(results);
    }

    [HttpPost("rate/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult> RateVideo(
        [FromRoute] string videoId,
        [FromQuery] string rating = "like",
        [FromQuery] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var ok = await client.RateVideoAsync(CleanVideoId(videoId), rating, userId ?? "fabian", cancellationToken).ConfigureAwait(false);
        return Ok(new { success = ok, videoId = CleanVideoId(videoId), rating });
    }

    [HttpPost("subscribe/{channelId}")]
    [AllowAnonymous]
    public async Task<ActionResult> Subscribe(
        [FromRoute] string channelId,
        [FromQuery] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var ok = await client.SubscribeChannelAsync(channelId, userId ?? "fabian", cancellationToken).ConfigureAwait(false);
        return Ok(new { success = ok, channelId });
    }

    [HttpPost("library/sync")]
    [AllowAnonymous]
    public async Task<ActionResult> SyncLibrary(
        [FromServices] Services.YouTubePlaylistAndCollectionSyncService playlistSyncService,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var count = await ScheduledTasks.SyncYouTubeStrmTask.PerformSyncAsync(client, Config, _logger, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        _ = Task.Run(() => playlistSyncService.SyncAllAsync(CancellationToken.None));
        return Ok(new { success = true, videosSynced = count });
    }

    private string CleanVideoId(string raw)
    {
        if (raw.StartsWith("ytm_", StringComparison.Ordinal))
        {
            return raw["ytm_".Length..];
        }

        if (raw.StartsWith("yt_", StringComparison.Ordinal))
        {
            return raw["yt_".Length..];
        }

        if (Guid.TryParse(raw, out var guid))
        {
            var item = _libraryManager.GetItemById(guid);
            if (item != null)
            {
                var vid = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
                if (!string.IsNullOrWhiteSpace(vid))
                {
                    return vid;
                }
            }
        }

        return raw;
    }
}
