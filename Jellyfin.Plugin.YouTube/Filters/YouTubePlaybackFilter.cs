using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.YouTube.Filters;

/// <summary>
/// Action and result filter intercepting native Jellyfin video / audio playback requests
/// to route YouTube media directly to native HTTP streams or Google CDN master playlists.
/// </summary>
public class YouTubePlaybackFilter : IAsyncActionFilter, IAsyncResultFilter
{
    private readonly ILibraryManager _libraryManager;

    public YouTubePlaybackFilter(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        var isVideoPath = path.Contains("/Videos/", StringComparison.OrdinalIgnoreCase) &&
                          (path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("/stream.m3u8", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("/master.m3u8", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("/main.m3u8", StringComparison.OrdinalIgnoreCase));
        var isAudioPath = path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase) &&
                          (path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("/stream.mp3", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith("/stream.m4a", StringComparison.OrdinalIgnoreCase));

        if (isVideoPath || isAudioPath)
        {
            if (context.ActionArguments.TryGetValue("itemId", out var rawId) && rawId is Guid id)
            {
                var item = _libraryManager.GetItemById(id);
                if (item != null)
                {
                    var isTrailer = item is Trailer ||
                                    (item.Path != null && (item.Path.Contains("-trailer", StringComparison.OrdinalIgnoreCase) ||
                                                          item.Path.Contains(".trailer.", StringComparison.OrdinalIgnoreCase)));

                    var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
                    if (string.IsNullOrWhiteSpace(videoId) && item.RemoteTrailers is { Count: > 0 })
                    {
                        foreach (var rt in item.RemoteTrailers)
                        {
                            var fromUrl = YouTubeIdKeys.ExtractVideoIdFromUrlOrText(rt.Url);
                            if (!string.IsNullOrWhiteSpace(fromUrl))
                            {
                                videoId = fromUrl;
                                isTrailer = true;
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(videoId))
                    {
                        var req = context.HttpContext.Request;
                        var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault();
                        var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
                        var scheme = !string.IsNullOrWhiteSpace(proto) ? proto : req.Scheme;
                        var serverUrl = Plugin.Instance?.Configuration.GetPublicServerUrl(scheme, host)
                            ?? (!string.IsNullOrWhiteSpace(host) ? $"{scheme}://{host}" : string.Empty);

                        var isAudio = isAudioPath || item is MediaBrowser.Controller.Entities.Audio.Audio ||
                                      (item.Path != null && (item.Path.Contains("YouTubeAudio", StringComparison.OrdinalIgnoreCase) ||
                                                             item.Path.Contains("/audio.m4a", StringComparison.OrdinalIgnoreCase)));
                        var trailerParam = isTrailer ? "?isTrailer=true" : string.Empty;
                        var target = isAudio
                            ? $"{serverUrl}/api/youtubePlugin/stream/{videoId}/audio.m4a"
                            : $"{serverUrl}/api/youtubePlugin/stream/{videoId}/master.m3u8{trailerParam}";

                        context.Result = new RedirectResult(target);
                        return;
                    }
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) &&
            context.Result is ObjectResult { Value: MediaBrowser.Model.MediaInfo.PlaybackInfoResponse pbResp } &&
            pbResp.MediaSources is { Count: > 0 })
        {
            var ytSources = pbResp.MediaSources
                .Where(m => m.Path != null && m.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ytSources.Count > 0)
            {
                var req = context.HttpContext.Request;
                var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault();
                var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
                var scheme = !string.IsNullOrWhiteSpace(proto) ? proto : req.Scheme;
                var serverUrl = Plugin.Instance?.Configuration.GetPublicServerUrl(scheme, host)
                    ?? (!string.IsNullOrWhiteSpace(host) ? $"{scheme}://{host}" : string.Empty);

                foreach (var ytSource in ytSources)
                {
                    if (!string.IsNullOrWhiteSpace(serverUrl))
                    {
                        ytSource.Path = RewritePluginUrlHost(ytSource.Path, serverUrl);
                        if (ytSource.MediaStreams != null)
                        {
                            foreach (var ms in ytSource.MediaStreams)
                            {
                                if (!string.IsNullOrWhiteSpace(ms.Path) && ms.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                                {
                                    ms.Path = RewritePluginUrlHost(ms.Path, serverUrl);
                                }

                                if (!string.IsNullOrWhiteSpace(ms.DeliveryUrl) && ms.DeliveryUrl.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase))
                                {
                                    ms.DeliveryUrl = RewritePluginUrlHost(ms.DeliveryUrl, serverUrl);
                                }
                            }
                        }
                    }

                    ytSource.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
                    ytSource.IsRemote = true;
                    ytSource.SupportsDirectPlay = true;
                    ytSource.SupportsDirectStream = true;
                    ytSource.SupportsTranscoding = true;
                }

                pbResp.MediaSources = ytSources;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static string RewritePluginUrlHost(string url, string serverUrl)
    {
        var idx = url.IndexOf("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? $"{serverUrl.TrimEnd('/')}{url[idx..]}" : url;
    }
}
