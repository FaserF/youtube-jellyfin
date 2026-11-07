using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers.LocalMetadata;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeMovieProvider(
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

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        if (!Config.EnableMovieSupport)
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
                YouTubeIdKeys.SetMovieId(r, videoId);
                results.Add(r);
            }
        }

        return results;
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        var config = Config;
        if (!config.EnableMovieSupport)
        {
            return result;
        }

        // Check local .info.json first
        if (config.EnableLocalMetadataProvider && !string.IsNullOrWhiteSpace(info.Path))
        {
            var jsonPath = YTDLInfoJsonHelper.FindInfoJsonPath(info.Path);
            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var ytdl = YTDLInfoJsonHelper.ReadInfoJson(jsonPath, cancellationToken);
                if (ytdl != null)
                {
                    var localMovie = new Movie();
                    YTDLInfoJsonHelper.PopulateMovie(localMovie, ytdl);
                    result.Item = localMovie;
                    result.HasMetadata = true;
                    return result;
                }
            }
        }

        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }

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

        var movie = new Movie
        {
            Name = meta.Title,
            Overview = YouTubeApiClient.FormatRichOverview(meta, meta.DislikeStats, meta.SponsorSegments),
            PremiereDate = meta.PublishedAt,
            ProductionYear = meta.PublishedAt?.Year,
            CommunityRating = rating,
            RunTimeTicks = meta.DurationSeconds > 0 ? TimeSpan.FromSeconds(meta.DurationSeconds).Ticks : null
        };

        if (!string.IsNullOrWhiteSpace(meta.ChannelTitle))
        {
            movie.AddStudio(meta.ChannelTitle);
        }

        YouTubeIdKeys.SetMovieId(movie, videoId);
        result.Item = movie;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
