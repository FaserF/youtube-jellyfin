using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTube.Configuration;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.YouTube.Search;

/// <summary>
/// Global MVC action filter that:
/// 1. Suppresses local YouTube library items from generic Jellyfin searches when
///    <see cref="PluginConfiguration.ShowYtInGlobalSearch"/> is false and the query has no "yt"/"youtube" keyword.
/// 2. Sets <c>X-YtSearch-SuppressAi: 1</c> response header when query has yt/youtube keyword,
///    so the AI Search plugin's injected JS skips its AI row for that request.
/// </summary>
public sealed class YouTubeGlobalSearchFilter : IAsyncActionFilter
{
    /// <summary>Response header that tells AI Search JS to suppress the AI row.</summary>
    public const string SuppressAiHeader = YouTubeSearchProvider.SuppressAiHeader;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await next().ConfigureAwait(false);

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return;
            }

            var http = context.HttpContext;
            if (http.Response.HasStarted)
            {
                return;
            }

            // Set suppress-AI header if the provider flagged this request.
            if (http.Items.ContainsKey(SuppressAiHeader))
            {
                http.Response.Headers[SuppressAiHeader] = "1";
                http.Response.Headers["Access-Control-Expose-Headers"] = SuppressAiHeader;
            }
        }
        catch (Exception)
        {
            // Never fail a search over a header nicety.
        }
    }
}
