using System.Security.Claims;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.YouTube.Helpers;

/// <summary>
/// Helper for extracting authenticated Jellyfin user identity and enforcing ownership/admin authorization.
/// </summary>
public static class ControllerAuthHelper
{
    public sealed record CallerIdentity(Guid UserId, string Username, bool IsAdmin);

    /// <summary>
    /// Extracts the authenticated Jellyfin user identity from the request principal.
    /// </summary>
    public static bool TryGetCaller(ClaimsPrincipal? principal, IUserManager userManager, out CallerIdentity? caller)
    {
        caller = null;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var userIdRaw = principal.FindFirst("Jellyfin-UserId")?.Value
                        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var parsedId = Guid.TryParse(userIdRaw, out var guid) ? guid : Guid.Empty;
        var jfUser = parsedId != Guid.Empty ? userManager.GetUserById(parsedId) : null;

        var username = jfUser?.Username
                       ?? principal.FindFirst("Jellyfin-User")?.Value
                       ?? principal.Identity.Name
                       ?? string.Empty;

        if (parsedId == Guid.Empty && string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var isAdmin = principal.IsInRole("Administrator") ||
                      string.Equals(principal.FindFirst("Jellyfin-IsApiKey")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        caller = new CallerIdentity(parsedId, username, isAdmin);
        return true;
    }

    /// <summary>
    /// Returns true if the caller is an administrator or owns the requested target user identifier.
    /// </summary>
    public static bool IsOwnerOrAdmin(CallerIdentity caller, string? requestedUserOrId)
    {
        if (caller.IsAdmin)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestedUserOrId))
        {
            return true;
        }

        var reqNorm = PluginConfiguration.NormalizeId(requestedUserOrId);
        var callerNameNorm = PluginConfiguration.NormalizeId(caller.Username);
        var callerIdNorm = caller.UserId != Guid.Empty
            ? PluginConfiguration.NormalizeId(caller.UserId.ToString("N"))
            : string.Empty;

        return (!string.IsNullOrEmpty(callerNameNorm) && string.Equals(reqNorm, callerNameNorm, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(callerIdNorm) && string.Equals(reqNorm, callerIdNorm, StringComparison.OrdinalIgnoreCase));
    }
}
