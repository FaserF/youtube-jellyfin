using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local image provider matching companion artwork (.webp, .jpg, .png, -poster, -thumb) next to media.
/// </summary>
public class YouTubeLocalImageProvider : ILocalImageProvider, IHasOrder
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<YouTubeLocalImageProvider> _logger;

    public YouTubeLocalImageProvider(IFileSystem fileSystem, ILogger<YouTubeLocalImageProvider> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public string Name => "YouTube (Local Artwork)";
    public int Order => 1;

    public bool Supports(BaseItem item) =>
        item is Episode or Movie or MusicVideo or Series or Season;

    public IEnumerable<LocalImageInfo> GetImages(BaseItem item, IDirectoryService directoryService)
    {
        var list = new List<LocalImageInfo>();
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return list;
        }

        var imageFiles = YTDLInfoJsonHelper.FindLocalImageFiles(item.Path);
        foreach (var imgPath in imageFiles)
        {
            try
            {
                var fileInfo = _fileSystem.GetFileSystemInfo(imgPath);
                if (fileInfo.Exists)
                {
                    var isBackdrop = imgPath.Contains("-poster", StringComparison.OrdinalIgnoreCase) ||
                                     imgPath.Contains("-fanart", StringComparison.OrdinalIgnoreCase) ||
                                     imgPath.Contains("-backdrop", StringComparison.OrdinalIgnoreCase);

                    list.Add(new LocalImageInfo
                    {
                        FileInfo = fileInfo,
                        Type = isBackdrop ? ImageType.Backdrop : ImageType.Primary
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve file info for image '{Path}'", imgPath);
            }
        }

        return list;
    }
}
