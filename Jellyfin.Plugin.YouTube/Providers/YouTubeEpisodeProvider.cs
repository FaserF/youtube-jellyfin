using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Providers.LocalMetadata;
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

    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }

        var config = Config;

        // Check local .info.json first if available
        if (config.EnableLocalMetadataProvider && !string.IsNullOrWhiteSpace(info.Path))
        {
            var jsonPath = YTDLInfoJsonHelper.FindInfoJsonPath(info.Path);
            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var ytdl = YTDLInfoJsonHelper.ReadInfoJson(jsonPath, cancellationToken);
                if (ytdl != null)
                {
                    var localEp = new Episode();
                    YTDLInfoJsonHelper.PopulateEpisode(localEp, ytdl);
                    result.Item = localEp;
                    result.HasMetadata = true;
                    return result;
                }
            }
        }

        var parsedEpNum = info.IndexNumber ?? ExtractEpisodeNumberFromPath(info.Path) ?? 1;
        var parsedSeasonNum = info.ParentIndexNumber ?? 1;
        var parentFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(info.Path)) ?? "YT");
        var cleanTitle = CleanEpisodeTitle(info.Name, videoId);
        var channelFromTitle = cleanTitle.Contains(" - ", StringComparison.Ordinal)
            ? cleanTitle.Split(" - ", 2)[0].Trim()
            : CleanFolderDisplayName(parentFolder);

        // D: Fetch real YT metadata for publish date, description, tags
        DateTime? publishedAt = null;
        string? richDescription = null;
        float communityRating = 7.5f;
        var tags = new List<string>();
        var category = string.Empty;

        try
        {
            using var client = CreateClient();

            // Try stream info first (has upload_date from yt-dlp, faster)
            var streamInfo = await client.ResolveStreamAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (streamInfo != null)
            {
                publishedAt = streamInfo.PublishedAt;
                channelFromTitle = !string.IsNullOrWhiteSpace(streamInfo.ChannelTitle) ? streamInfo.ChannelTitle : channelFromTitle;
                tags = streamInfo.Tags.Take(10).ToList();
                category = streamInfo.Category;

                if (streamInfo.DislikeStats != null && streamInfo.DislikeStats.Likes + streamInfo.DislikeStats.Dislikes > 0)
                {
                    var total = streamInfo.DislikeStats.Likes + streamInfo.DislikeStats.Dislikes;
                    communityRating = (float)Math.Round(streamInfo.DislikeStats.Likes / (double)total * 10, 1);
                    communityRating = Math.Clamp(communityRating, 0f, 10f);
                }

                // E: Rich description = stats header + blank line + YT description
                richDescription = YouTubeApiClient.FormatRichOverview(
                    new Models.YouTubeVideoItem
                    {
                        VideoId = videoId,
                        Title = cleanTitle,
                        ChannelTitle = streamInfo.ChannelTitle,
                        Description = streamInfo.Description,
                        PublishedTimeText = streamInfo.PublishedAt?.ToString("yyyy-MM-dd") ?? string.Empty,
                        DislikeStats = streamInfo.DislikeStats,
                    },
                    streamInfo.DislikeStats,
                    streamInfo.SponsorSegments);

                richDescription += $"\n\n🔗 https://www.youtube.com/watch?v={videoId}";
            }

            // Fallback: YouTube Data API for more accurate publish date
            if (publishedAt == null)
            {
                var meta = await client.GetVideoMetadataAsync(videoId, cancellationToken).ConfigureAwait(false);
                if (meta?.PublishedAt != null)
                {
                    publishedAt = meta.PublishedAt;
                    if (string.IsNullOrWhiteSpace(richDescription))
                    {
                        richDescription = YouTubeApiClient.FormatRichOverview(meta, meta.DislikeStats, meta.SponsorSegments);
                        richDescription += $"\n\n🔗 https://www.youtube.com/watch?v={videoId}";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout — use fallback
        }

        var premiereDate = publishedAt ?? DateTime.UtcNow.Date;
        var overview = richDescription ?? $"👤 {channelFromTitle}  •  🔗 https://www.youtube.com/watch?v={videoId}\n\nYouTube Ad-Free Stream ({videoId})";

        var episode = new Episode
        {
            Name = cleanTitle,
            Overview = overview,
            IndexNumber = parsedEpNum,
            ParentIndexNumber = parsedSeasonNum,
            PremiereDate = premiereDate,
            ProductionYear = premiereDate.Year,
            CommunityRating = communityRating
        };

        // F: Map YT channel → Jellyfin Studio
        if (!string.IsNullOrWhiteSpace(channelFromTitle))
        {
            episode.AddStudio(channelFromTitle);
        }

        // F: Map YT category → Genre
        if (!string.IsNullOrWhiteSpace(category))
        {
            episode.AddGenre(category);
        }

        episode.AddGenre("YouTube");

        // F: Map YT tags → Jellyfin Tags (up to 10)
        if (tags.Count > 0)
        {
            episode.Tags = tags.Take(10).ToArray();
        }

        YouTubeIdKeys.SetVideoId(episode, videoId);
        episode.SetProviderId("YouTubeEpisodeKey", $"{parentFolder}_{videoId}");

        result.Item = episode;
        result.HasMetadata = true;
        return result;
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
