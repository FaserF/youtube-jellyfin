using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTube.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        EnableStreamingChannel = true;
        EnablePublicFallbackForUnlinkedUsers = true;
        PrimaryJellyfinUser = string.Empty;
        PrimaryJellyfinUserId = string.Empty;
        PreferredLanguage = "de-DE";
        PreferredRegion = "DE";
        PreferredMaxResolution = 2160;
        EnableSponsorBlock = true;
        SponsorBlockCategories = "sponsor,selfpromo,interaction,intro,outro";
        EnableReturnYouTubeDislike = true;
        EnablePlayheadSync = true;
        EnableFavoriteToLikeSync = true;
        EnableYouTubeLibrary = true;
        EnableStrmSync = true;
        EnableMovieSupport = true;
        EnableTrailerSupport = true;
        EnableAutoTrailerStrmSync = true;
        TrailerLanguage = "de-DE";
        EnableYouTubeMusic = true;
        EnableYouTubeMusicChannel = true;
        EnableYouTubeMusicLibrary = true;
        EnableYouTubeMusicPlaylistsSync = true;
        YouTubeMusicMaxItemsPerFeed = 25;
        YouTubeMusicPreferredBitrate = 256;
        PublicServerUrl = string.Empty;
        YouTubeMusicLibraryPath = "/data/youtube-music";
        FetchThumbnails = true;
        StrmLibraryPath = "/data/youtube";
        ProxyUrl = string.Empty;
        YtDlpPath = "/usr/local/bin/yt-dlp";
        OAuthClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
        OAuthClientSecret = "SboVhoG9s0rNafixCSGGKXAT";
        PersistedRefreshToken = string.Empty;
        PersistedAccessToken = string.Empty;
        PersistedTokenExpiresAtUnix = 0;
        CookiesText = string.Empty;
        UserAccounts = new List<YouTubeUserAccountEntry>();
    }

    public bool EnableStreamingChannel { get; set; }
    public bool EnablePublicFallbackForUnlinkedUsers { get; set; }
    public string PrimaryJellyfinUser { get; set; }
    public string PrimaryJellyfinUserId { get; set; }
    public string PreferredLanguage { get; set; }
    public string PreferredRegion { get; set; }
    public int PreferredMaxResolution { get; set; }
    public bool EnableSponsorBlock { get; set; }
    public string SponsorBlockCategories { get; set; }
    public bool EnableReturnYouTubeDislike { get; set; }
    public bool EnablePlayheadSync { get; set; }
    public bool EnableFavoriteToLikeSync { get; set; }
    public bool EnableYouTubeLibrary { get; set; }
    public bool EnableStrmSync { get; set; }
    public bool EnableMovieSupport { get; set; }
    public bool EnableTrailerSupport { get; set; }
    public bool EnableAutoTrailerStrmSync { get; set; }
    public string TrailerLanguage { get; set; }
    public bool EnableYouTubeMusic { get; set; }
    public bool EnableYouTubeMusicChannel { get; set; }
    public bool EnableYouTubeMusicLibrary { get; set; }
    public bool EnableYouTubeMusicPlaylistsSync { get; set; }
    public int YouTubeMusicMaxItemsPerFeed { get; set; }
    public int YouTubeMusicPreferredBitrate { get; set; }
    public string YouTubeMusicLibraryPath { get; set; }
    public string PublicServerUrl { get; set; }
    public bool FetchThumbnails { get; set; }
    public string StrmLibraryPath { get; set; }
    public string ProxyUrl { get; set; }
    public string YtDlpPath { get; set; }
    public string OAuthClientId { get; set; }
    public string OAuthClientSecret { get; set; }
    public string PersistedRefreshToken { get; set; }
    public string PersistedAccessToken { get; set; }
    public long PersistedTokenExpiresAtUnix { get; set; }
    public string CookiesText { get; set; }
    public List<YouTubeUserAccountEntry> UserAccounts { get; set; }

    public YouTubeUserAccountEntry? GetAccountForUser(string? usernameOrId)
    {
        var normalized = NormalizeId(usernameOrId);
        if (!string.IsNullOrWhiteSpace(normalized) && UserAccounts != null)
        {
            var match = UserAccounts.FirstOrDefault(u =>
                string.Equals(NormalizeId(u.JellyfinUser), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeId(u.JellyfinUserId), normalized, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.IsNullOrWhiteSpace(match.RefreshToken))
            {
                return match;
            }
        }

        if (IsPrimaryUser(usernameOrId) && !string.IsNullOrWhiteSpace(PersistedRefreshToken))
        {
            return new YouTubeUserAccountEntry
            {
                JellyfinUser = PrimaryJellyfinUser,
                JellyfinUserId = PrimaryJellyfinUserId,
                AccountName = !string.IsNullOrWhiteSpace(PrimaryJellyfinUser) ? $"{PrimaryJellyfinUser} (Primary)" : "Primary Account",
                ClientId = OAuthClientId,
                ClientSecret = OAuthClientSecret,
                RefreshToken = PersistedRefreshToken,
                AccessToken = PersistedAccessToken,
                TokenExpiresAtUnix = PersistedTokenExpiresAtUnix
            };
        }

        return null;
    }

    public bool IsPrimaryUser(string? usernameOrId)
    {
        if (string.IsNullOrWhiteSpace(usernameOrId) || (string.IsNullOrWhiteSpace(PrimaryJellyfinUser) && string.IsNullOrWhiteSpace(PrimaryJellyfinUserId)))
        {
            return false;
        }

        var norm = NormalizeId(usernameOrId);
        return (!string.IsNullOrWhiteSpace(PrimaryJellyfinUser) && string.Equals(norm, NormalizeId(PrimaryJellyfinUser), StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(PrimaryJellyfinUserId) && string.Equals(norm, NormalizeId(PrimaryJellyfinUserId), StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeId(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    public string GetPublicServerUrl(string? requestScheme = null, string? requestHost = null)
    {
        if (!string.IsNullOrWhiteSpace(requestScheme) && !string.IsNullOrWhiteSpace(requestHost))
        {
            return $"{requestScheme}://{requestHost}".TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(PublicServerUrl))
        {
            return PublicServerUrl.TrimEnd('/');
        }

        var published = Environment.GetEnvironmentVariable("JELLYFIN_PublishedServerUrl");
        if (!string.IsNullOrWhiteSpace(published))
        {
            return published.TrimEnd('/');
        }

        return "https://jellyfin.fabiseitz.de";
    }
}

public class YouTubeUserAccountEntry
{
    public string JellyfinUser { get; set; } = string.Empty;
    public string JellyfinUserId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ChannelHandle { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public long TokenExpiresAtUnix { get; set; }
}
