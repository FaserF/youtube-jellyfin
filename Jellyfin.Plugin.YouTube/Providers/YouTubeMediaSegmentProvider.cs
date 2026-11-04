using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Providers;

/// <summary>
/// MediaSegment and Chapter provider for YouTube videos and episodes.
/// Automatically creates Jellyfin Intro, Outro, and Commercial skip segments from SponsorBlock
/// and native YouTube chapters so Android TV, Web, and mobile clients show native skip buttons.
/// </summary>
public class YouTubeMediaSegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<YouTubeMediaSegmentProvider> _logger;

    public YouTubeMediaSegmentProvider(
        ILibraryManager libraryManager,
        IChapterManager chapterManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<YouTubeMediaSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _chapterManager = chapterManager;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public string Name => "YouTube SponsorBlock";

    public ValueTask<bool> Supports(BaseItem item)
    {
        if (item is not Episode && item is not Video)
        {
            return ValueTask.FromResult(false);
        }

        var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
        return ValueTask.FromResult(!string.IsNullOrWhiteSpace(videoId));
    }

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegmentDto>();
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item == null)
        {
            return segments;
        }

        var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds) ?? YouTubeIdKeys.ExtractVideoIdFromPath(item.Path);
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return segments;
        }

        try
        {
            using var client = new YouTubeApiClient(
                _httpClientFactory.CreateClient("YouTube"),
                _loggerFactory.CreateLogger<YouTubeApiClient>());

            var streamInfoTask = client.ResolveStreamAsync(videoId, cancellationToken);
            var sbTask = client.GetSponsorBlockSegmentsAsync(videoId, cancellationToken);
            await Task.WhenAll(streamInfoTask, sbTask).ConfigureAwait(false);

            var streamInfo = await streamInfoTask.ConfigureAwait(false);
            var sbSegments = await sbTask.ConfigureAwait(false);

            // 1. Map SponsorBlock segments to Jellyfin MediaSegments (Intro, Outro, Commercial)
            foreach (var sb in sbSegments)
            {
                if (sb.EndSeconds <= sb.StartSeconds)
                {
                    continue;
                }

                var segType = MapSponsorCategoryToSegmentType(sb.Category);
                if (segType == MediaSegmentType.Unknown)
                {
                    continue;
                }

                segments.Add(new MediaSegmentDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    Type = segType,
                    StartTicks = TimeSpan.FromSeconds(sb.StartSeconds).Ticks,
                    EndTicks = TimeSpan.FromSeconds(sb.EndSeconds).Ticks
                });
            }

            // 2. Map native YouTube chapters to MediaSegments (if Intro/Outro in chapter titles)
            var chaptersList = new List<ChapterInfo>();
            if (streamInfo?.Chapters is { Count: > 0 } ytChapters)
            {
                foreach (var ch in ytChapters)
                {
                    var startTicks = TimeSpan.FromSeconds(ch.StartSeconds).Ticks;
                    chaptersList.Add(new ChapterInfo
                    {
                        Name = ch.Title,
                        StartPositionTicks = startTicks
                    });

                    var titleLower = ch.Title.ToLowerInvariant();
                    if ((titleLower.Contains("intro") || titleLower.Contains("einleitung")) && ch.EndSeconds > ch.StartSeconds)
                    {
                        if (!segments.Any(s => s.Type == MediaSegmentType.Intro))
                        {
                            segments.Add(new MediaSegmentDto
                            {
                                Id = Guid.NewGuid(),
                                ItemId = item.Id,
                                Type = MediaSegmentType.Intro,
                                StartTicks = startTicks,
                                EndTicks = TimeSpan.FromSeconds(ch.EndSeconds).Ticks
                            });
                        }
                    }
                    else if ((titleLower.Contains("outro") || titleLower.Contains("abspann") || titleLower.Contains("credits")) && ch.EndSeconds > ch.StartSeconds)
                    {
                        if (!segments.Any(s => s.Type == MediaSegmentType.Outro))
                        {
                            segments.Add(new MediaSegmentDto
                            {
                                Id = Guid.NewGuid(),
                                ItemId = item.Id,
                                Type = MediaSegmentType.Outro,
                                StartTicks = startTicks,
                                EndTicks = TimeSpan.FromSeconds(ch.EndSeconds).Ticks
                            });
                        }
                    }
                }
            }

            // Also add SponsorBlock segment markers as chapters if no uploader chapters exist
            if (chaptersList.Count == 0 && sbSegments.Count > 0)
            {
                foreach (var sb in sbSegments.OrderBy(s => s.StartSeconds))
                {
                    chaptersList.Add(new ChapterInfo
                    {
                        Name = $"[Skip: {sb.Category}]",
                        StartPositionTicks = TimeSpan.FromSeconds(sb.StartSeconds).Ticks
                    });
                }
            }

            // Save chapters into Jellyfin so timeline scrub markers are rendered
            if (chaptersList.Count > 0 && _chapterManager.Supports(item))
            {
                try
                {
                    _chapterManager.SaveChapters(item, chaptersList);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not persist chapters for {ItemId}", item.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve media segments/chapters for {VideoId}", videoId);
        }

        return segments;
    }

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private static MediaSegmentType MapSponsorCategoryToSegmentType(string category) =>
        category.ToLowerInvariant() switch
        {
            "intro" => MediaSegmentType.Intro,
            "outro" => MediaSegmentType.Outro,
            "sponsor" => MediaSegmentType.Commercial,
            "selfpromo" => MediaSegmentType.Commercial,
            "interaction" => MediaSegmentType.Commercial,
            "music_offtopic" => MediaSegmentType.Commercial,
            "preview" => MediaSegmentType.Preview,
            "recap" => MediaSegmentType.Recap,
            _ => MediaSegmentType.Commercial
        };
}
