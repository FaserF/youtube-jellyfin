using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.ScheduledTasks;

public class ClearYouTubeIDsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ClearYouTubeIDsTask> _logger;

    private static readonly string[] AllYouTubeKeys =
    {
        YouTubeIdKeys.Video,
        YouTubeIdKeys.Channel,
        YouTubeIdKeys.Playlist,
        YouTubeIdKeys.Movie,
        YouTubeIdKeys.MusicVideo,
        "YouTube"
    };

    public ClearYouTubeIDsTask(ILibraryManager libraryManager, ILogger<ClearYouTubeIDsTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Clear YouTube IDs";
    public string Key => "ClearYouTubeIDsTask";
    public string Description => "Removes all YouTube external provider IDs from Series, Seasons, Episodes, and Movies.";
    public string Category => "YouTube";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[]
            {
                BaseItemKind.Series,
                BaseItemKind.Season,
                BaseItemKind.Episode,
                BaseItemKind.Movie,
                BaseItemKind.MusicVideo
            },
            Recursive = true
        });

        var total = items.Count;
        var clearedCount = 0;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var modified = false;

            if (item.ProviderIds != null)
            {
                foreach (var key in AllYouTubeKeys)
                {
                    if (item.ProviderIds.Remove(key))
                    {
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                clearedCount++;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            if (total > 0)
            {
                progress.Report((double)(i + 1) / total * 100.0);
            }
        }

        _logger.LogInformation("Cleared YouTube provider IDs from {ClearedCount} items", clearedCount);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
