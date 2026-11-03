using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public YouTubeEpisodeProvider(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public string Name => "YouTube";
    public int Order => 3;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private YouTubeApiClient CreateClient() =>
        new(_httpClientFactory.CreateClient("YouTube"), _loggerFactory.CreateLogger<YouTubeApiClient>());

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var videoId = YouTubeIdKeys.GetVideoId(searchInfo.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(searchInfo.Path);

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            using var client = CreateClient();
            var meta = await client.GetVideoMetadataAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (meta != null)
            {
                var r = new RemoteSearchResult
                {
                    Name = meta.Title,
                    Overview = meta.Description,
                    PremiereDate = meta.PublishedAt,
                    ImageUrl = meta.ThumbnailUrl,
                    SearchProviderName = Name
                };
                YouTubeIdKeys.SetVideoId(r, videoId);
                results.Add(r);
            }
        }

        return results;
    }

    public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Task.FromResult(result);
        }

        var config = Config;
        var parsedEpNum = info.IndexNumber ?? ExtractEpisodeNumberFromPath(info.Path) ?? 1;
        var parsedSeasonNum = info.ParentIndexNumber ?? 1;
        var parentFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(info.Path)) ?? "YT");
        var cleanTitle = CleanEpisodeTitle(info.Name, videoId);
        var channelFromTitle = cleanTitle.Contains(" - ", StringComparison.Ordinal)
            ? cleanTitle.Split(" - ", 2)[0].Trim()
            : CleanFolderDisplayName(parentFolder);

        var isUpcomingShow = parentFolder.Contains("Upcoming", StringComparison.OrdinalIgnoreCase);
        var premiereDate = isUpcomingShow
            ? DateTime.UtcNow.Date.AddDays((parsedEpNum % 7) + 1)
            : DateTime.UtcNow.Date;

        var episode = new Episode
        {
            Name = cleanTitle,
            Overview = $"👤 {channelFromTitle}  •  🔗 https://www.youtube.com/watch?v={videoId}\n\nYouTube SmartTube Ad-Free Stream ({videoId})",
            IndexNumber = parsedEpNum,
            ParentIndexNumber = parsedSeasonNum,
            PremiereDate = premiereDate,
            ProductionYear = premiereDate.Year,
            CommunityRating = 8.5f,
            Width = 1920,
            Height = 1080
        };

        if (!string.IsNullOrWhiteSpace(channelFromTitle))
        {
            episode.AddStudio(channelFromTitle);
        }

        episode.AddGenre("YouTube");

        // Ensure every episode has a unique ProviderId so Jellyfin never merges S01E01 across different YouTube channels!
        YouTubeIdKeys.SetVideoId(episode, videoId);
        episode.SetProviderId("YouTubeEpisodeKey", $"{parentFolder}_{videoId}");

        result.Item = episode;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    private static string CleanFolderDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "YouTube";
        }

        var idx = rawName.IndexOf("[YouTube", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? rawName[..idx].Trim() : rawName.Trim();
    }

    private static int? ExtractEpisodeNumberFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"S\d+E(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static string CleanEpisodeTitle(string? rawName, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallbackId;
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(rawName, @"^S\d+E\d+\s*-\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var idx = cleaned.IndexOf("[YouTubeVideo-", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? cleaned[..idx].Trim() : cleaned.Trim();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        CreateClient().HttpClient.GetAsync(url, cancellationToken);
}
