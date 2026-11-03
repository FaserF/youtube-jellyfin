using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeImageProvider(
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

    public bool Supports(BaseItem item) => item is Series or Season or Episode or Movie;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return item switch
        {
            Series or Movie => new[] { ImageType.Primary, ImageType.Backdrop },
            Season => new[] { ImageType.Primary },
            Episode => new[] { ImageType.Primary },
            _ => Array.Empty<ImageType>()
        };
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        if (!Config.FetchThumbnails)
        {
            return list;
        }

        if (item is Episode or Movie)
        {
            var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds)
                ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
            if (!string.IsNullOrWhiteSpace(videoId))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Type = ImageType.Primary,
                    Url = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg"
                });
                list.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Type = ImageType.Primary,
                    Url = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
                });
                if (item is Movie)
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Backdrop,
                        Url = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg"
                    });
                }
            }

            return list;
        }

        var channelId = YouTubeIdKeys.GetChannelId(item.ProviderIds)
            ?? YouTubeIdKeys.ExtractChannelIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(channelId) && item is Season season && season.Series != null)
        {
            channelId = YouTubeIdKeys.GetChannelId(season.Series.ProviderIds);
        }

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            using var client = CreateClient();
            var ch = await client.GetChannelMetadataAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (ch != null)
            {
                if (!string.IsNullOrWhiteSpace(ch.ThumbnailUrl))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Primary,
                        Url = ch.ThumbnailUrl
                    });
                }

                if (!string.IsNullOrWhiteSpace(ch.BannerUrl) && item is Series)
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Backdrop,
                        Url = ch.BannerUrl
                    });
                }
            }
        }

        return list;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
