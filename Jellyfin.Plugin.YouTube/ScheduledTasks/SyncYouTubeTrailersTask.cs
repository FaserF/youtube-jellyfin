using System.Security;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.ScheduledTasks;

public class SyncYouTubeTrailersTask : IScheduledTask
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncYouTubeTrailersTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SyncYouTubeTrailersTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<SyncYouTubeTrailersTask> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Sync Movie & TV Trailers from YouTube (.strm)";
    public string Key => "SyncYouTubeTrailersTask";
    public string Description => "Searches official YouTube trailers for Movies and TV Series in your Jellyfin library and creates lightweight companion -trailer.strm files for native 1-click playback.";
    public string Category => "YouTube";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(5);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
        await PerformSyncAsync(client, config, _libraryManager, _logger, cancellationToken).ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();
        progress.Report(100);
    }

    public static async Task<int> PerformSyncAsync(
        YouTubeApiClient client,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!config.EnableTrailerSupport || !config.EnableAutoTrailerStrmSync)
        {
            logger.LogInformation("Trailer synchronization is disabled in YouTube plugin settings");
            return 0;
        }

        var totalAdded = 0;
        var lang = config.TrailerLanguage ?? config.PreferredLanguage ?? "de-DE";
        var isGerman = lang.StartsWith("de", StringComparison.OrdinalIgnoreCase);

        // 1. Process Movies
        try
        {
            var movies = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false
            }).OfType<Movie>().ToList();

            logger.LogInformation("Checking trailers for {Count} movies in library", movies.Count);
            foreach (var movie in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(movie.Path) || !File.Exists(movie.Path))
                {
                    continue;
                }

                var movieDir = Path.GetDirectoryName(movie.Path);
                if (string.IsNullOrWhiteSpace(movieDir) || !Directory.Exists(movieDir))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(movie.Path);
                var trailerStrm = Path.Combine(movieDir, $"{baseName}-trailer.strm");
                var trailerNfo = Path.Combine(movieDir, $"{baseName}-trailer.nfo");

                // Check if movie already has any local trailer file
                if (File.Exists(trailerStrm) ||
                    Directory.GetFiles(movieDir, "*-trailer.*").Length > 0 ||
                    Directory.GetFiles(movieDir, "trailer.*").Length > 0 ||
                    (Directory.Exists(Path.Combine(movieDir, "trailers")) && Directory.GetFiles(Path.Combine(movieDir, "trailers")).Length > 0))
                {
                    continue; // Already has local trailer
                }

                string? videoId = null;

                // A: Try extracting from movie.RemoteTrailers
                if (movie.RemoteTrailers is { Count: > 0 })
                {
                    foreach (var rt in movie.RemoteTrailers)
                    {
                        var fromUrl = YouTubeIdKeys.ExtractVideoIdFromUrlOrText(rt.Url);
                        if (!string.IsNullOrWhiteSpace(fromUrl))
                        {
                            videoId = fromUrl;
                            break;
                        }
                    }
                }

                // B: Fallback to YouTube anonymous search
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    var query = isGerman
                        ? $"{movie.Name} {movie.ProductionYear} Offizieller Trailer Deutsch"
                        : $"{movie.Name} {movie.ProductionYear} Official Trailer";
                    var results = await client.SearchVideosAsync(query, null, 5, cancellationToken).ConfigureAwait(false);
                    var candidate = results.FirstOrDefault(r => r.Title.Contains("trailer", StringComparison.OrdinalIgnoreCase)) ?? results.FirstOrDefault();
                    videoId = candidate?.VideoId;
                }

                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    var strmContent = $"http://127.0.0.1:8096/api/youtubePlugin/stream/{videoId}/master.m3u8?isTrailer=true\n";
                    await File.WriteAllTextAsync(trailerStrm, strmContent, cancellationToken).ConfigureAwait(false);

                    var nfoContent = $@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<episodedetails>
  <title>{SecurityElement.Escape(movie.Name)} - Trailer</title>
  <plot>{SecurityElement.Escape($"Offizieller Trailer für {movie.Name} ({movie.ProductionYear})")}</plot>
  <uniqueid type=""youtube"">{videoId}</uniqueid>
</episodedetails>";
                    await File.WriteAllTextAsync(trailerNfo, nfoContent, cancellationToken).ConfigureAwait(false);
                    totalAdded++;
                    logger.LogInformation("Added YouTube trailer for movie '{Name}' ({Year}): videoId={VideoId}", movie.Name, movie.ProductionYear, videoId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error syncing movie trailers");
        }

        // 2. Process Series
        try
        {
            var seriesList = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                IsVirtualItem = false
            }).OfType<Series>().ToList();

            logger.LogInformation("Checking trailers for {Count} series in library", seriesList.Count);
            foreach (var series in seriesList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(series.Path) || !Directory.Exists(series.Path))
                {
                    continue;
                }

                var trailerStrm = Path.Combine(series.Path, "trailer.strm");
                var trailerNfo = Path.Combine(series.Path, "trailer.nfo");

                if (File.Exists(trailerStrm) ||
                    Directory.GetFiles(series.Path, "*-trailer.*").Length > 0 ||
                    Directory.GetFiles(series.Path, "trailer.*").Length > 0 ||
                    (Directory.Exists(Path.Combine(series.Path, "trailers")) && Directory.GetFiles(Path.Combine(series.Path, "trailers")).Length > 0))
                {
                    continue; // Already has local trailer
                }

                string? videoId = null;
                if (series.RemoteTrailers is { Count: > 0 })
                {
                    foreach (var rt in series.RemoteTrailers)
                    {
                        var fromUrl = YouTubeIdKeys.ExtractVideoIdFromUrlOrText(rt.Url);
                        if (!string.IsNullOrWhiteSpace(fromUrl))
                        {
                            videoId = fromUrl;
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(videoId))
                {
                    var query = isGerman
                        ? $"{series.Name} {series.ProductionYear} Offizieller Serie Trailer Deutsch"
                        : $"{series.Name} {series.ProductionYear} Official TV Series Trailer";
                    var results = await client.SearchVideosAsync(query, null, 5, cancellationToken).ConfigureAwait(false);
                    var candidate = results.FirstOrDefault(r => r.Title.Contains("trailer", StringComparison.OrdinalIgnoreCase)) ?? results.FirstOrDefault();
                    videoId = candidate?.VideoId;
                }

                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    var strmContent = $"http://127.0.0.1:8096/api/youtubePlugin/stream/{videoId}/master.m3u8?isTrailer=true\n";
                    await File.WriteAllTextAsync(trailerStrm, strmContent, cancellationToken).ConfigureAwait(false);

                    var nfoContent = $@"<?xml version=""1.0"" encoding=""utf-8"" standalone=""yes""?>
<episodedetails>
  <title>{SecurityElement.Escape(series.Name)} - Trailer</title>
  <plot>{SecurityElement.Escape($"Offizieller Trailer für {series.Name} ({series.ProductionYear})")}</plot>
  <uniqueid type=""youtube"">{videoId}</uniqueid>
</episodedetails>";
                    await File.WriteAllTextAsync(trailerNfo, nfoContent, cancellationToken).ConfigureAwait(false);
                    totalAdded++;
                    logger.LogInformation("Added YouTube trailer for series '{Name}': videoId={VideoId}", series.Name, videoId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error syncing series trailers");
        }

        logger.LogInformation("YouTube trailer sync finished: {Added} new trailers written", totalAdded);
        return totalAdded;
    }
}
