using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local metadata provider for Series using channel .info.json.
/// </summary>
public class YouTubeLocalSeriesProvider : AbstractYouTubeLocalProvider<Series>
{
    public YouTubeLocalSeriesProvider(ILogger<YouTubeLocalSeriesProvider> logger) : base(logger)
    {
    }

    protected override void PopulateMetadata(Series item, YTDLData data, ItemInfo info)
    {
        YTDLInfoJsonHelper.PopulateSeries(item, data);
    }
}
