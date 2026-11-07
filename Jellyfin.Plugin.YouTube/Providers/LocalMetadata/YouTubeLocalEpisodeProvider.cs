using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local metadata provider for Episodes using companion .info.json.
/// </summary>
public class YouTubeLocalEpisodeProvider : AbstractYouTubeLocalProvider<Episode>
{
    public YouTubeLocalEpisodeProvider(ILogger<YouTubeLocalEpisodeProvider> logger) : base(logger)
    {
    }

    protected override void PopulateMetadata(Episode item, YTDLData data, ItemInfo info)
    {
        YTDLInfoJsonHelper.PopulateEpisode(item, data);
    }
}
