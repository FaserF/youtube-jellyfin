using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local metadata provider for Music Videos using companion .info.json.
/// </summary>
public class YouTubeLocalMusicVideoProvider : AbstractYouTubeLocalProvider<MusicVideo>
{
    public YouTubeLocalMusicVideoProvider(ILogger<YouTubeLocalMusicVideoProvider> logger) : base(logger)
    {
    }

    protected override void PopulateMetadata(MusicVideo item, YTDLData data, ItemInfo info)
    {
        YTDLInfoJsonHelper.PopulateMusicVideo(item, data);
    }
}
