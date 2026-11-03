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

    public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var channelId = YouTubeIdKeys.GetChannelId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractChannelIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(channelId))
        {
            if (!string.IsNullOrWhiteSpace(info.Path) && info.Path.Contains("/youtube", StringComparison.OrdinalIgnoreCase))
            {
                var folderKey = Path.GetFileName(info.Path.TrimEnd('/', '\\'));
                var feedSeries = new Series
                {
                    Name = info.Name,
                    Overview = $"YouTube SmartTube Feed: {info.Name}",
                    ProductionYear = DateTime.UtcNow.Year
                };
                feedSeries.AddStudio("YouTube");
                feedSeries.AddGenre("YouTube");
                YouTubeIdKeys.SetChannelId(feedSeries, $"FEED_{folderKey}");
                result.Item = feedSeries;
                result.HasMetadata = true;
            }

            return Task.FromResult(result);
        }

        var cleanTitle = CleanFolderDisplayName(info.Name);
        var series = new Series
        {
            Name = cleanTitle,
            Overview = $"YouTube Channel: {cleanTitle} (https://www.youtube.com/channel/{channelId})",
            ProductionYear = DateTime.UtcNow.Year
        };
        series.AddStudio("YouTube");
        series.AddGenre("YouTube");
        YouTubeIdKeys.SetChannelId(series, channelId);

        result.Item = series;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    private static string CleanFolderDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "YouTube Channel";
        }

        var idx = rawName.IndexOf("[YouTubeChannel-", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? rawName[..idx].Trim() : rawName.Trim();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
