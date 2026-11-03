using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeEpisodeProvider(
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

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
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
                    ImageUrl = meta.ThumbnailUrl,
                    SearchProviderName = Name
                };
                YouTubeIdKeys.SetVideoId(r, videoId);
                results.Add(r);
            }
        }

        return results;
    }

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }

        var config = Config;
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
            rating = (float)Math.Round((double)meta.DislikeStats.Likes / (meta.DislikeStats.Likes + meta.DislikeStats.Dislikes) * 10.0, 1);
        }

        var episode = new Episode
        {
            Name = meta.Title,
            Overview = YouTubeApiClient.FormatRichOverview(meta, meta.DislikeStats, meta.SponsorSegments),
            IndexNumber = info.IndexNumber,
            ParentIndexNumber = info.ParentIndexNumber ?? 1,
            PremiereDate = meta.PublishedAt,
            ProductionYear = meta.PublishedAt?.Year,
            CommunityRating = rating,
            RunTimeTicks = meta.DurationSeconds > 0 ? TimeSpan.FromSeconds(meta.DurationSeconds).Ticks : null,
            Width = 1920,
            Height = 1080
        };

        YouTubeIdKeys.SetVideoId(episode, videoId);
        if (!string.IsNullOrWhiteSpace(meta.ChannelId))
        {
            YouTubeIdKeys.SetChannelId(episode, meta.ChannelId);
        }

        result.Item = episode;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
