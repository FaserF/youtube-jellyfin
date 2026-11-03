using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
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

    public bool Supports(BaseItem item) => item is Series or Season or Episode or Movie or Audio or MusicAlbum or MusicArtist;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return item switch
        {
            Series or Movie or MusicArtist => new[] { ImageType.Primary, ImageType.Backdrop },
            Season or MusicAlbum => new[] { ImageType.Primary },
            Episode or Audio => new[] { ImageType.Primary },
            _ => Array.Empty<ImageType>()
        };
    }

    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        if (!Config.FetchThumbnails)
        {
            return Task.FromResult<IEnumerable<RemoteImageInfo>>(list);
        }

        if (item is Episode or Movie or Audio)
        {
            var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds)
                ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
            if (!string.IsNullOrWhiteSpace(videoId))
            {
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
                        Url = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg"
                    });
                }
            }

            return Task.FromResult<IEnumerable<RemoteImageInfo>>(list);
        }

        // For Series / Season: use the first video's thumbnail in the folder immediately (zero API quota, 100% reliable)
        if (!string.IsNullOrWhiteSpace(item.Path) && Directory.Exists(item.Path))
        {
            try
            {
                var firstStrm = Directory.EnumerateFiles(item.Path, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
                var vid = YouTubeIdKeys.ExtractVideoIdFromPath(firstStrm);
                if (!string.IsNullOrWhiteSpace(vid))
                {
                    list.Add(new RemoteImageInfo
                    {
                        ProviderName = Name,
                        Type = ImageType.Primary,
                        Url = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg"
                    });
                    if (item is Series or MusicArtist)
                    {
                        list.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Type = ImageType.Backdrop,
                            Url = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg"
                        });
                    }
                }
            }
            catch
            {
                // Ignore directory scan errors
            }
        }

        return Task.FromResult<IEnumerable<RemoteImageInfo>>(list);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
