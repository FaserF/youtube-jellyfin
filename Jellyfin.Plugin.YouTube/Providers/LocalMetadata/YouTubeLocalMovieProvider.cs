using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Local metadata provider for Movies using companion .info.json.
/// </summary>
public class YouTubeLocalMovieProvider : AbstractYouTubeLocalProvider<Movie>
{
    public YouTubeLocalMovieProvider(ILogger<YouTubeLocalMovieProvider> logger) : base(logger)
    {
    }

    protected override void PopulateMetadata(Movie item, YTDLData data, ItemInfo info)
    {
        YTDLInfoJsonHelper.PopulateMovie(item, data);
    }
}
