using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.ScheduledTasks;

/// <summary>
/// Post-scan library task and scheduled task that sorts YouTube series episodes chronologically by upload date.
/// </summary>
public class YouTubeEpisodeIndexerTask : ILibraryPostScanTask, IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<YouTubeEpisodeIndexerTask> _logger;

    public YouTubeEpisodeIndexerTask(
        ILibraryManager libraryManager,
        ILogger<YouTubeEpisodeIndexerTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "YouTube Episode Indexer";
    public string Key => "YouTubeEpisodeIndexerTask";
    public string Description => "Sorts YouTube channel episodes chronologically by upload date and assigns sequential episode numbering.";
    public string Category => "YouTube";

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await ExecuteAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableEpisodeIndexer)
        {
            return;
        }

        progress.Report(0);
        _logger.LogInformation("YouTube Plugin: Starting chronological episode reindexing...");

        var shows = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true
        }).OfType<Series>().Where(s => YouTubeIdKeys.IsYouTubeItem(s, s.Path)).ToList();

        var showCount = shows.Count;
        for (var i = 0; i < showCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var show = shows[i];
            await IndexShowAsync(show, cancellationToken).ConfigureAwait(false);
            progress.Report((double)(i + 1) / showCount * 100);
        }

        _logger.LogInformation("YouTube Plugin: Completed episode reindexing for {Count} YouTube shows", showCount);
    }

    private async Task IndexShowAsync(Series show, CancellationToken cancellationToken)
    {
        var seasons = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = show.Id,
            IncludeItemTypes = new[] { BaseItemKind.Season },
            DtoOptions = new DtoOptions()
        }).OfType<Season>().ToList();

        seasons.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        var sindex = 1;
        foreach (var season in seasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            season.IndexNumber = sindex;
            await _libraryManager.UpdateItemAsync(season, show, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { season.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                DtoOptions = new DtoOptions()
            }).OfType<Episode>().ToList();

            // Sort chronologically by PremiereDate (upload date)
            episodes.Sort((x, y) =>
            {
                if (!x.PremiereDate.HasValue && !y.PremiereDate.HasValue)
                {
                    return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                }
                if (!x.PremiereDate.HasValue)
                {
                    return -1;
                }
                if (!y.PremiereDate.HasValue)
                {
                    return 1;
                }
                var cmp = x.PremiereDate.Value.CompareTo(y.PremiereDate.Value);
                return cmp != 0 ? cmp : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });

            var eindex = 1;
            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                episode.IndexNumber = eindex;
                episode.ParentIndexNumber = sindex;
                await _libraryManager.UpdateItemAsync(episode, season, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                eindex++;
            }

            sindex++;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
}
