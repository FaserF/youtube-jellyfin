using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.ScheduledTasks;

public class SyncYouTubeWatchHistoryTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncYouTubeWatchHistoryTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SyncYouTubeWatchHistoryTask(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<SyncYouTubeWatchHistoryTask> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Sync YouTube Watch History to Jellyfin";
    public string Key => "SyncYouTubeWatchHistoryTask";
    public string Description => "Syncs YouTube watch progress and watched video status into Jellyfin user profiles.";
    public string Category => "YouTube";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnablePlayheadSync)
        {
            progress.Report(100);
            return;
        }

        using var client = new YouTubeApiClient(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());
        var accounts = new List<YouTubeUserAccountEntry>();
        if (config.UserAccounts != null)
        {
            accounts.AddRange(config.UserAccounts.Where(a => !string.IsNullOrWhiteSpace(a.RefreshToken)));
        }

        if (accounts.Count == 0 && !string.IsNullOrWhiteSpace(config.PersistedRefreshToken))
        {
            var primary = config.GetAccountForUser(config.PrimaryJellyfinUser);
            if (primary != null)
            {
                accounts.Add(primary);
            }
        }

        if (accounts.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var allLibraryItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
            Recursive = true
        });

        for (var i = 0; i < accounts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[i];
            if (!Guid.TryParse(account.JellyfinUserId, out var userGuid))
            {
                continue;
            }

            try
            {
                var historyItems = await client.GetBrowseFeedAsync("FEhistory", account.JellyfinUser, 60, cancellationToken).ConfigureAwait(false);
                var updated = 0;

                foreach (var ytItem in historyItems)
                {
                    if (string.IsNullOrWhiteSpace(ytItem.VideoId))
                    {
                        continue;
                    }

                    var candidate = allLibraryItems.FirstOrDefault(e =>
                        string.Equals(YouTubeIdKeys.GetVideoId(e.ProviderIds), ytItem.VideoId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(YouTubeIdKeys.ExtractVideoIdFromPath(e.Path), ytItem.VideoId, StringComparison.OrdinalIgnoreCase));

                    if (candidate == null)
                    {
                        continue;
                    }

                    var user = _userManager.GetUserById(userGuid);
                    if (user == null)
                    {
                        continue;
                    }

                    var userData = _userDataManager.GetUserData(user, candidate);
                    if (userData == null)
                    {
                        continue;
                    }

                    var modified = false;
                    if (ytItem.ResumePercent >= 90)
                    {
                        if (!userData.Played)
                        {
                            userData.Played = true;
                            userData.PlaybackPositionTicks = 0;
                            userData.PlayCount = Math.Max(1, userData.PlayCount);
                            userData.LastPlayedDate = DateTime.UtcNow;
                            modified = true;
                        }
                    }
                    else if (ytItem.ResumePercent > 0 && ytItem.DurationSeconds > 0)
                    {
                        var targetSeconds = ytItem.DurationSeconds * (ytItem.ResumePercent / 100.0);
                        var targetTicks = TimeSpan.FromSeconds(targetSeconds).Ticks;
                        if (Math.Abs(userData.PlaybackPositionTicks - targetTicks) > TimeSpan.FromSeconds(5).Ticks)
                        {
                            userData.Played = false;
                            userData.PlaybackPositionTicks = targetTicks;
                            userData.LastPlayedDate = DateTime.UtcNow;
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        _userDataManager.SaveUserData(
                            user,
                            candidate,
                            userData,
                            userData.Played ? UserDataSaveReason.PlaybackFinished : UserDataSaveReason.PlaybackProgress,
                            cancellationToken);
                        updated++;
                    }
                }

                _logger.LogInformation("YouTube history sync for user '{User}': {Updated} items updated", account.JellyfinUser, updated);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync YouTube history for user '{User}'", account.JellyfinUser);
            }

            progress.Report((double)(i + 1) / accounts.Count * 100.0);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(30).Ticks
            }
        };
    }
}
