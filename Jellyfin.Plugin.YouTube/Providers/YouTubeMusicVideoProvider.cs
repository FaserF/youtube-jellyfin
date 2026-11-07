using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers.LocalMetadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

/// <summary>
/// Remote metadata provider for MusicVideo items.
/// </summary>
public class YouTubeMusicVideoProvider : IRemoteMetadataProvider<MusicVideo, MusicVideoInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeMusicVideoProvider(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube";
    public int Order => 3;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MusicVideoInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        if (!Config.EnableMusicVideoSupport)
        {
            return results;
        }

        var videoId = YouTubeIdKeys.GetVideoId(searchInfo.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(searchInfo.Path);

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            using var client = CreateClient();
            var meta = await client.GetVideoMetadataAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (meta != null)
            {
                var r = new RemoteSearchResult
                {
                    Name = meta.Title,
                    Overview = meta.Description,
                    PremiereDate = meta.PublishedAt,
                    ProductionYear = meta.PublishedAt?.Year,
                    ImageUrl = meta.ThumbnailUrl,
                    SearchProviderName = Name
                };
                YouTubeIdKeys.SetMusicVideoId(r, videoId);
                results.Add(r);
            }
        }

        return results;
    }

    public async Task<MetadataResult<MusicVideo>> GetMetadata(MusicVideoInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicVideo>();
        var config = Config;
        if (!config.EnableMusicVideoSupport)
        {
            return result;
        }

        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        // 1. Check local .info.json first if file exists
        if (config.EnableLocalMetadataProvider && !string.IsNullOrWhiteSpace(info.Path))
        {
            var jsonPath = YTDLInfoJsonHelper.FindInfoJsonPath(info.Path);
            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var ytdl = YTDLInfoJsonHelper.ReadInfoJson(jsonPath, cancellationToken);
                if (ytdl != null)
                {
                    var localMv = new MusicVideo();
                    YTDLInfoJsonHelper.PopulateMusicVideo(localMv, ytdl);
                    result.Item = localMv;
                    result.HasMetadata = true;
                    return result;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }

        // 2. Fetch remote YouTube metadata
        using var client = CreateClient();
        var meta = await client.GetVideoMetadataAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (meta == null)
        {
            return result;
        }

        if (config.EnableReturnYouTubeDislike)
        {
            meta.DislikeStats = await client.GetReturnYouTubeDislikeAsync(videoId, cancellationToken).ConfigureAwait(false);
        }

        if (config.EnableSponsorBlock)
        {
            meta.SponsorSegments = await client.GetSponsorBlockSegmentsAsync(videoId, cancellationToken).ConfigureAwait(false);
        }

        float? rating = null;
        if (meta.DislikeStats != null && (meta.DislikeStats.Likes + meta.DislikeStats.Dislikes) > 0)
        {
            var total = meta.DislikeStats.Likes + meta.DislikeStats.Dislikes;
            rating = (float)Math.Round(meta.DislikeStats.Likes / (double)total * 10, 1);
            rating = Math.Clamp(rating.Value, 0f, 10f);
        }

        var mv = new MusicVideo
        {
            Name = meta.Title,
            Overview = YouTubeApiClient.FormatRichOverview(meta, meta.DislikeStats, meta.SponsorSegments),
            PremiereDate = meta.PublishedAt,
            ProductionYear = meta.PublishedAt?.Year,
            CommunityRating = rating ?? 8.0f
        };

        if (!string.IsNullOrWhiteSpace(meta.ChannelTitle))
        {
            mv.AddStudio(meta.ChannelTitle);
            mv.Artists = new[] { meta.ChannelTitle };
        }

        mv.AddGenre("YouTube");
        mv.AddGenre("Music Video");

        YouTubeIdKeys.SetMusicVideoId(mv, videoId);
        result.Item = mv;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
