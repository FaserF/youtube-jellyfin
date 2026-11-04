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
        using var stream = resourceName != null ? assembly.GetManifestResourceStream(resourceName) : null;
        if (stream == null)
        {
            return NotFound("// youtubePlugin.js resource not found");
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
        return resp == null
            ? StatusCode(StatusCodes.Status502BadGateway, new { error = "Failed to request YouTube TV device code" })
            : Ok(resp);
    }

    [HttpPost("auth/poll")]
    [AllowAnonymous]
    public async Task<ActionResult> PollDeviceCode(
        [FromQuery] string deviceCode,
        [FromQuery] string? jellyfinUser = null,
        [FromQuery] string? jellyfinUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return BadRequest(new { error = "Missing deviceCode" });
        }

        var targetUser = !string.IsNullOrWhiteSpace(jellyfinUser) ? jellyfinUser : (Config.PrimaryJellyfinUser ?? string.Empty);
        using var client = CreateClient();
        var (success, error, account) = await client.PollDeviceTokenAsync(deviceCode, targetUser, jellyfinUserId, cancellationToken).ConfigureAwait(false);
        return !success
            ? Ok(new { pending = true, error })
            : Ok(new { pending = false, success = true, jellyfinUser = account?.JellyfinUser, accountName = account?.AccountName });
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
                        jfUsers.Add(new { id = uid, username = uname, linkedAccount = linkedAcc, isLinked = !string.IsNullOrWhiteSpace(linkedAcc) });
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
        config.UserAccounts?.RemoveAll(u =>
            string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUser), PluginConfiguration.NormalizeId(jellyfinUser), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUserId), PluginConfiguration.NormalizeId(jellyfinUser), StringComparison.OrdinalIgnoreCase));

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
    public async Task<ActionResult<YouTubeStreamInfoDto>> GetStreamInfo([FromRoute] string videoId, CancellationToken cancellationToken)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        return info == null ? NotFound(new { error = $"Could not resolve YouTube stream for {cleanId}" }) : Ok(info);
    }

    [HttpGet("stream/{videoId}/play")]
    [HttpGet("stream/{videoId}/master.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> PlayOrProxyStream(
        [FromRoute] string videoId,
        [FromQuery] bool isTrailer = false,
        CancellationToken cancellationToken = default)
    {
        var cleanId = CleanVideoId(videoId);
        using var client = CreateClient();
        var info = await client.ResolveStreamAsync(cleanId, cancellationToken).ConfigureAwait(false);
        if (info == null)
        {
            return NotFound(new { error = $"Unable to resolve YouTube stream for {cleanId}" });
        }

        // Direct YouTube streaming: 302 Redirect directly to Google CDN
        // Client connects directly to YouTube edge servers without passing bytes through Jellyfin!
        var directUrl = !string.IsNullOrWhiteSpace(info.HlsManifestUrl)
            ? info.HlsManifestUrl
            : info.DirectStreamUrl;

        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            return Redirect(directUrl);
        }

        return NotFound(new { error = $"No streaming URL available for {cleanId}" });
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
        if (!string.IsNullOrWhiteSpace(formatId) && info.AudioTracks.Count > 0)
        {
            audioUrl = info.AudioTracks.FirstOrDefault(a => string.Equals(a.FormatId, formatId, StringComparison.OrdinalIgnoreCase))?.StreamUrl;
        }

        if (string.IsNullOrWhiteSpace(audioUrl) && !string.IsNullOrWhiteSpace(lang) && info.AudioTracks.Count > 0)
        {
            audioUrl = info.AudioTracks
                .OrderByDescending(a => a.Codec == "aac")
                .ThenByDescending(a => a.Bitrate)
                .FirstOrDefault(a => a.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase))?.StreamUrl;
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            var bestAacTrack = info.AudioTracks.Where(a => a.Codec == "aac").OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.Bitrate).FirstOrDefault();
            audioUrl = !string.IsNullOrWhiteSpace(info.AudioStreamUrl)
                ? info.AudioStreamUrl
                : (bestAacTrack?.StreamUrl ?? info.AudioTracks.FirstOrDefault(a => a.IsDefault)?.StreamUrl ?? info.DirectStreamUrl);
        }

        if (!string.IsNullOrWhiteSpace(audioUrl))
        {
            return Redirect(audioUrl);
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

        var matchTrack = info.SubtitleTracks.FirstOrDefault(s => string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase))
                         ?? info.SubtitleTracks.FirstOrDefault(s => s.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
        var subUrl = matchTrack?.StreamUrl;

        if (string.IsNullOrWhiteSpace(subUrl) && !info.Subtitles.TryGetValue(lang, out subUrl))
        {
            subUrl = info.Subtitles.FirstOrDefault(k => k.Key.StartsWith(lang, StringComparison.OrdinalIgnoreCase)).Value
                     ?? info.Subtitles.Values.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(subUrl))
        {
            return Content("WEBVTT\n\n", "text/vtt; charset=utf-8");
        }

        try
        {
            if (subUrl.Contains("/api/timedtext", StringComparison.OrdinalIgnoreCase) && !subUrl.Contains("fmt=vtt", StringComparison.OrdinalIgnoreCase))
            {
                subUrl += (subUrl.Contains('?') ? "&" : "?") + "fmt=vtt";
            }

            var rawSub = await client.HttpClient.GetStringAsync(subUrl, cancellationToken).ConfigureAwait(false);
            return Content(rawSub.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) ? rawSub : "WEBVTT\n\n" + rawSub, "text/vtt; charset=utf-8");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Subtitle fetch failed for {VideoId} ({Lang})", cleanId, lang);
            return Content("WEBVTT\n\n", "text/vtt; charset=utf-8");
        }
    }

    [HttpGet("proxy")]
    [HttpGet("proxy/{filename}")]
    [AllowAnonymous]
    public ActionResult ProxyUrl(
        [FromRoute] string? filename = null,
        [FromQuery] string? u = null)
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
        catch (FormatException)
        {
            targetUrl = Uri.UnescapeDataString(u);
        }

        if (!targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        // Direct stream: 302 Redirect to Google CDN so client streams directly from YouTube
        return Redirect(targetUrl);
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

    private string RewriteM3u8Urls(string m3u8Content, string baseUrl)
    {
        var baseUri = new Uri(baseUrl);
        var sb = new StringBuilder();
        var lines = m3u8Content.Split('\n');
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
                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) && line.Contains("TYPE=SUBTITLES", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    line = System.Text.RegularExpressions.Regex.Replace(line, @",SUBTITLES=""[^""]*""", string.Empty);
                }

                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase) && line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    var isPrefLang = line.Contains($"LANGUAGE=\"{prefLang}", StringComparison.OrdinalIgnoreCase);
                    var isOriginal = line.Contains("original", StringComparison.OrdinalIgnoreCase) || line.Contains("LANGUAGE=\"en", StringComparison.OrdinalIgnoreCase);
                    var shouldKeep = isPrefLang || isOriginal || (!hasPrefAudio && line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase));
                    if (!shouldKeep)
                    {
                        continue;
                    }

                    var groupMatch = System.Text.RegularExpressions.Regex.Match(line, @"GROUP-ID=""([^""]+)""");
                    var groupId = groupMatch.Success ? groupMatch.Groups[1].Value : "default";

                    if (!defaultAudioSetForGroup.Contains(groupId))
                    {
                        if (isPrefLang || (!hasPrefAudio && isOriginal))
                        {
                            line = line.Contains("DEFAULT=NO", StringComparison.OrdinalIgnoreCase)
                                ? line.Replace("DEFAULT=NO", "DEFAULT=YES", StringComparison.OrdinalIgnoreCase)
                                : (!line.Contains("DEFAULT=", StringComparison.OrdinalIgnoreCase) ? line + ",DEFAULT=YES" : line);
                            defaultAudioSetForGroup.Add(groupId);
                        }
                    }
                    else if (line.Contains("DEFAULT=YES", StringComparison.OrdinalIgnoreCase))
                    {
                        line = line.Replace("DEFAULT=YES", "DEFAULT=NO", StringComparison.OrdinalIgnoreCase);
                    }
                }

                // Rewrite URI="..." to direct Google CDN absolute URLs for direct streaming
                var uriUpdated = System.Text.RegularExpressions.Regex.Replace(line, @"URI=""([^""]+)""", m =>
                {
                    var abs = new Uri(baseUri, m.Groups[1].Value).ToString();
                    return $"URI=\"{abs}\"";
                });
                sb.AppendLine(uriUpdated);
            }
            else
            {
                var abs = new Uri(baseUri, line).ToString();
                sb.AppendLine(abs);
            }
        }

        return sb.ToString();
    }

    [HttpGet("sponsorblock/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<List<SponsorBlockSegmentDto>>> GetSponsorBlock([FromRoute] string videoId, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        return Ok(await client.GetSponsorBlockSegmentsAsync(CleanVideoId(videoId), cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("ryd/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<ReturnYouTubeDislikeDto>> GetDislikes([FromRoute] string videoId, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var clean = CleanVideoId(videoId);
        return Ok(await client.GetReturnYouTubeDislikeAsync(clean, cancellationToken).ConfigureAwait(false) ?? new ReturnYouTubeDislikeDto { Id = clean });
    }

    [HttpGet("comments/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult<List<YouTubeCommentDto>>> GetComments([FromRoute] string videoId, [FromQuery] string? userId = null, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        return Ok(await client.GetVideoCommentsAsync(CleanVideoId(videoId), userId, 30, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<ActionResult<List<YouTubeVideoItem>>> Search([FromQuery] string q, [FromQuery] string? userId = null, [FromQuery] int limit = 25, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(new List<YouTubeVideoItem>());
        }

        using var client = CreateClient();
        return Ok(await client.SearchVideosAsync(q, userId, limit, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("rate/{videoId}")]
    [AllowAnonymous]
    public async Task<ActionResult> RateVideo([FromRoute] string videoId, [FromQuery] string rating = "like", [FromQuery] string? userId = null, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var clean = CleanVideoId(videoId);
        var targetUser = userId ?? Config.PrimaryJellyfinUser;
        var ok = await client.RateVideoAsync(clean, rating, targetUser, cancellationToken).ConfigureAwait(false);
        return Ok(new { success = ok, videoId = clean, rating });
    }

    [HttpPost("subscribe/{channelId}")]
    [AllowAnonymous]
    public async Task<ActionResult> Subscribe([FromRoute] string channelId, [FromQuery] string? userId = null, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var targetUser = userId ?? Config.PrimaryJellyfinUser;
        var ok = await client.SubscribeChannelAsync(channelId, targetUser, cancellationToken).ConfigureAwait(false);
        return Ok(new { success = ok, channelId });
    }

    [HttpPost("library/sync")]
    [AllowAnonymous]
    public async Task<ActionResult> SyncLibrary([FromServices] Services.YouTubePlaylistAndCollectionSyncService playlistSyncService, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var count = await ScheduledTasks.SyncYouTubeStrmTask.PerformSyncAsync(client, Config, _logger, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        _ = Task.Run(() => playlistSyncService.SyncAllAsync(CancellationToken.None));
        return Ok(new { success = true, videosSynced = count });
    }

    [HttpPost("trailers/sync")]
    [AllowAnonymous]
    public async Task<ActionResult> SyncTrailers(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var count = await ScheduledTasks.SyncYouTubeTrailersTask.PerformSyncAsync(client, Config, _libraryManager, _logger, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        return Ok(new { success = true, trailersSynced = count });
    }

    [HttpGet("trailers/{itemId}")]
    [AllowAnonymous]
    public async Task<ActionResult> GetItemTrailer([FromRoute] string itemId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var id) || _libraryManager.GetItemById(id) is not { } item)
        {
            return NotFound(new { error = "Item not found" });
        }

        var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(videoId) && item.RemoteTrailers is { Count: > 0 })
        {
            videoId = item.RemoteTrailers.Select(rt => YouTubeIdKeys.ExtractVideoIdFromUrlOrText(rt.Url)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            using var client = CreateClient();
            var lang = Config.TrailerLanguage ?? "de-DE";
            var query = lang.StartsWith("de", StringComparison.OrdinalIgnoreCase)
                ? $"{item.Name} {item.ProductionYear} Offizieller Trailer Deutsch"
                : $"{item.Name} {item.ProductionYear} Official Trailer";
            var results = await client.SearchVideosAsync(query, null, 3, cancellationToken).ConfigureAwait(false);
            videoId = results.FirstOrDefault()?.VideoId;
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return NotFound(new { error = "No trailer found" });
        }

        var streamUrl = $"/api/youtubePlugin/stream/{videoId}/master.m3u8?isTrailer=true";
        return Ok(new { success = true, itemId, videoId, streamUrl });
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

        if (Guid.TryParse(raw, out var guid) && _libraryManager.GetItemById(guid) is { } item)
        {
            return YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path) ?? raw;
        }

        return raw;
    }
}
