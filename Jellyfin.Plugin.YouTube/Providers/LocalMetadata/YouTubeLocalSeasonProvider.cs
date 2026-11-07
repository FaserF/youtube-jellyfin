using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local metadata provider for Seasons using .info.json.
/// </summary>
public class YouTubeLocalSeasonProvider : AbstractYouTubeLocalProvider<Season>
{
    public YouTubeLocalSeasonProvider(ILogger<YouTubeLocalSeasonProvider> logger) : base(logger)
    {
    }

    protected override void PopulateMetadata(Season item, YTDLData data, ItemInfo info)
    {
        YTDLInfoJsonHelper.PopulateSeason(item, data);
    }
}
