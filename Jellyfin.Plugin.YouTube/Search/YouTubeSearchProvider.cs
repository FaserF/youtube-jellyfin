using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Search;

/// <summary>
/// Implements <see cref="IInternalSearchProvider"/> to inject YouTube search results
/// into Jellyfin's native search pipeline (/Search/Hints, /Items) when
/// <see cref="PluginConfiguration.YtSearchMode"/> is set to "NativeSearch".
/// <para>
/// Logic:
/// - If query contains "youtube" or "yt" keyword → always run YT search, suppress AI Search row.
/// - If no keyword and YtSearchOnNoResults=true → run YT search only when local results are empty.
/// - ShowYtInGlobalSearch=false (default) → no YT items in generic searches without keyword.
/// </para>
/// </summary>
public sealed class YouTubeSearchProvider : IInternalSearchProvider
{
    /// <summary>Response header the filter sets to tell the AI Search JS to skip its row.</summary>
    public const string SuppressAiHeader = "X-YtSearch-SuppressAi";

    private static readonly Regex YtKeywordRegex = new(@"\byt\b|\byoutube\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<YouTubeSearchProvider> _logger;

    /// <summary>Initializes a new instance of the <see cref="YouTubeSearchProvider"/> class.</summary>
    public YouTubeSearchProvider(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IHttpContextAccessor httpContextAccessor,
        ILoggerFactory loggerFactory,
        ILogger<YouTubeSearchProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _httpContextAccessor = httpContextAccessor;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "YouTube Search";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 60; // slightly below AI Search (50), above SqlSearchProvider (100)

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Returns true when the search term contains "yt" or "youtube" as a word.</summary>
    public static bool IsYouTubeQuery(string? term)
    {
        return !string.IsNullOrWhiteSpace(term) && YtKeywordRegex.IsMatch(term);
    }

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query)
    {
        var config = Config;

        // Only activate in NativeSearch mode.
        if (!string.Equals(config.YtSearchMode, "NativeSearch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasYtKeyword = IsYouTubeQuery(query.SearchTerm);

        if (hasYtKeyword)
        {
            // YT-intent query: always run, and signal AI Search to suppress its row.
            if (config.YtSearchSuppressAiWhenYtKeyword)
            {
                var ctx = _httpContextAccessor.HttpContext;
                if (ctx != null)
                {
                    ctx.Items[SuppressAiHeader] = true;
                }
            }

            return true;
        }

        // No keyword: only run if ShowYtInGlobalSearch is explicitly enabled,
        // or if YtSearchOnNoResults is on (we check for 0 results at SearchAsync time).
        return config.ShowYtInGlobalSearch || config.YtSearchOnNoResults;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var config = Config;
        var hasYtKeyword = IsYouTubeQuery(query.SearchTerm);

        // Without keyword: only run if ShowYtInGlobalSearch is on, OR if no local results exist.
        if (!hasYtKeyword)
        {
            if (!config.ShowYtInGlobalSearch)
            {
                if (!config.YtSearchOnNoResults)
                {
                    return Array.Empty<SearchResult>();
                }

                // Check whether local library has results first.
                if (HasLocalResults(query))
                {
                    return Array.Empty<SearchResult>();
                }
            }
        }

        // Strip "youtube" / "yt" keywords from the actual search term sent to YouTube.
        var cleanTerm = hasYtKeyword
            ? YtKeywordRegex.Replace(query.SearchTerm ?? string.Empty, string.Empty).Trim().Trim(':').Trim()
            : query.SearchTerm ?? string.Empty;

        if (string.IsNullOrWhiteSpace(cleanTerm))
        {
            return Array.Empty<SearchResult>();
        }

        try
        {
            using var client = new YouTubeApiClient(
                _httpClientFactory.CreateClient("YouTube"),
                _loggerFactory.CreateLogger<YouTubeApiClient>());

            var userId = query.UserId is Guid uid && uid != Guid.Empty ? uid.ToString("N") : null;
            var videos = await client.SearchVideosAsync(cleanTerm, userId, config.YtSearchMaxResults, cancellationToken).ConfigureAwait(false);

            // Convert results: YouTube search results are virtual (non-local).
            // We return them as SearchResult with a virtual/negative score so they
            // appear after local matches but are still surfaced to external clients.
            var results = new List<SearchResult>(videos.Count);
            foreach (var video in videos)
            {
                // Try to find a matching local item first (by video ID in ProviderIds).
                var localId = FindLocalYouTubeItem(video.VideoId);
                if (localId.HasValue)
                {
                    // Local item found: surface it at the top of YT results.
                    results.Add(new SearchResult(localId.Value, 25f));
                }
                else if (config.YtSearchShowNonLocalResults)
                {
                    // Non-local: we can't return a SearchResult without a real ItemId.
                    // These are surfaced via the /api/youtubePlugin/search endpoint in SearchTab mode
                    // or via the response header for the web client JS to render separately.
                    // Log for diagnostics only.
                    _logger.LogDebug("YT search: non-local result skipped for NativeSearch mode: {VideoId} - {Title}", video.VideoId, video.Title);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTubeSearchProvider: search failed for query '{Query}'", cleanTerm);
            return Array.Empty<SearchResult>();
        }
    }

    private bool HasLocalResults(SearchProviderQuery query)
    {
        try
        {
            var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                SearchTerm = query.SearchTerm,
                Limit = 1,
                Recursive = true,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false)
            });
            return items.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HasLocalResults check failed");
            return false;
        }
    }

    private Guid? FindLocalYouTubeItem(string videoId)
    {
        try
        {
            var items = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { ["YouTubeId"] = videoId },
                Limit = 1,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false)
            });
            return items.Count > 0 ? items[0].Id : null;
        }
        catch
        {
            return null;
        }
    }
}
