using Jellyfin.Plugin.YouTube.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeSeasonProvider(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube";
    public int Order => 3;

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());

    public Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season>();
        var channelId = YouTubeIdKeys.GetChannelId(info.SeriesProviderIds)
            ?? YouTubeIdKeys.ExtractChannelIdFromPath(info.Path);
        var playlistId = YouTubeIdKeys.GetPlaylistId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractPlaylistIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(channelId) && string.IsNullOrWhiteSpace(playlistId) &&
            (string.IsNullOrWhiteSpace(info.Path) || !info.Path.Contains("/youtube/", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(result);
        }

        var season = new Season
        {
            IndexNumber = info.IndexNumber ?? 1,
            Name = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : $"Season {info.IndexNumber ?? 1}"
        };

        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            YouTubeIdKeys.SetPlaylistId(season, playlistId);
        }

        result.Item = season;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
