using Jellyfin.Plugin.YouTube.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeSeriesProvider> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeSeriesProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<YouTubeSeriesProvider> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube";
    public int Order => 3;

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        using var client = CreateClient();

        var channelId = YouTubeIdKeys.GetChannelId(searchInfo.ProviderIds)
            ?? YouTubeIdKeys.ExtractChannelIdFromPath(searchInfo.Path);

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var ch = await client.GetChannelMetadataAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (ch != null)
            {
                var r = new RemoteSearchResult
                {
                    Name = ch.Title,
                    Overview = ch.Description,
                    ImageUrl = ch.ThumbnailUrl,
                    SearchProviderName = Name
                };
                YouTubeIdKeys.SetChannelId(r, ch.ChannelId);
                results.Add(r);
            }
        }

        return results;
    }

    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var channelId = YouTubeIdKeys.GetChannelId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractChannelIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(channelId))
        {
            if (!string.IsNullOrWhiteSpace(info.Path) && info.Path.Contains("/youtube/", StringComparison.OrdinalIgnoreCase))
            {
                var feedSeries = new Series
                {
                    Name = info.Name,
                    Overview = $"YouTube SmartTube Feed: {info.Name}"
                };
                feedSeries.AddStudio("YouTube");
                feedSeries.AddGenre("YouTube");
                result.Item = feedSeries;
                result.HasMetadata = true;
            }

            return result;
        }

        using var client = CreateClient();
        var ch = await client.GetChannelMetadataAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (ch == null)
        {
            return result;
        }

        var series = new Series
        {
            Name = !string.IsNullOrWhiteSpace(ch.Title) ? ch.Title : info.Name,
            Overview = ch.Description
        };
        series.AddStudio("YouTube");
        series.AddGenre("YouTube");
        YouTubeIdKeys.SetChannelId(series, channelId);

        result.Item = series;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
