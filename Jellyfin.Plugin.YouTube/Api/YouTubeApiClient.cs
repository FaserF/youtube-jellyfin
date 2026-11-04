using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTube.Api;

public partial class YouTubeApiClient : IDisposable
{
    public const string DefaultTvClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
    public const string DefaultTvClientSecret = "SboVhoG9s0rNafixCSGGKXAT";
    private const string TvUserAgent = "Mozilla/5.0 (Linux; Tizen 5.0; SAMSUNG SM-T830) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.31 TV Safari/537.36";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<YouTubeApiClient> _logger;
    private static readonly SemaphoreSlim _authLock = new(1, 1);

    private static readonly ConcurrentDictionary<string, (object Data, DateTime CachedAt)> _cache = new();
    private static readonly ConcurrentDictionary<string, (YouTubeStreamInfoDto Info, DateTime CachedAt)> _streamCache = new();
    private static readonly ConcurrentDictionary<string, (List<SponsorBlockSegmentDto> Segments, DateTime CachedAt)> _sbCache = new();
    private static readonly ConcurrentDictionary<string, (ReturnYouTubeDislikeDto Stats, DateTime CachedAt)> _rydCache = new();

    private static readonly ConcurrentDictionary<string, HttpClient> _sharedProxyClients = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public YouTubeApiClient(HttpClient httpClient, ILogger<YouTubeApiClient> logger)
    {
        _logger = logger;
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var proxyUrl = config.ProxyUrl;

        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            try
            {
                var normalizedProxy = proxyUrl.Replace("socks5h://", "socks5://", StringComparison.OrdinalIgnoreCase);
                _httpClient = _sharedProxyClients.GetOrAdd(normalizedProxy, pUrl =>
                {
                    var handler = new SocketsHttpHandler
                    {
                        Proxy = new WebProxy(pUrl),
                        UseProxy = true,
                        AutomaticDecompression = DecompressionMethods.All,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                        MaxConnectionsPerServer = 64
                    };
                    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", TvUserAgent);
                    return client;
                });
                _ownsHttpClient = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to configure proxy {ProxyUrl}, using default HttpClient", proxyUrl);
                _httpClient = httpClient;
            }
        }
        else
        {
            _httpClient = httpClient;
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", TvUserAgent);
        }
    }

    public HttpClient HttpClient => _httpClient;

    public async Task<YouTubeDeviceCodeResponse?> RequestDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            client_id = DefaultTvClientId,
            scope = "http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content",
            device_id = Guid.NewGuid().ToString(),
            device_model = "YtLr::Shell"
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/o/oauth2/device/code")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<YouTubeDeviceCodeResponse>(json);
    }

    public async Task<(bool Success, string? Error, YouTubeUserAccountEntry? Account)> PollDeviceTokenAsync(
        string deviceCode,
        string jellyfinUser,
        string? jellyfinUserId = null,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = DefaultTvClientId,
            ["client_secret"] = DefaultTvClientSecret,
            ["code"] = deviceCode,
            ["grant_type"] = "http://oauth.net/grant_type/device/1.0"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/o/oauth2/token")
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errProp))
        {
            return (false, errProp.GetString(), null);
        }

        if (!root.TryGetProperty("access_token", out var atProp) ||
            !root.TryGetProperty("refresh_token", out var rtProp))
        {
            return (false, "Invalid token response", null);
        }

        var accessToken = atProp.GetString() ?? string.Empty;
        var refreshToken = rtProp.GetString() ?? string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt64() : 3600;
        var expiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn - 60;

        var accountName = await FetchAccountNameAsync(accessToken, cancellationToken).ConfigureAwait(false) ?? jellyfinUser;

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        config.UserAccounts ??= new List<YouTubeUserAccountEntry>();
        var existing = config.UserAccounts.FirstOrDefault(u =>
            string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUser), PluginConfiguration.NormalizeId(jellyfinUser), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(jellyfinUserId) && string.Equals(PluginConfiguration.NormalizeId(u.JellyfinUserId), PluginConfiguration.NormalizeId(jellyfinUserId), StringComparison.OrdinalIgnoreCase)));

        if (existing == null)
        {
            existing = new YouTubeUserAccountEntry
            {
                JellyfinUser = jellyfinUser,
                JellyfinUserId = jellyfinUserId ?? string.Empty
            };
            config.UserAccounts.Add(existing);
        }

        existing.AccountName = accountName;
        existing.ClientId = DefaultTvClientId;
        existing.ClientSecret = DefaultTvClientSecret;
        existing.RefreshToken = refreshToken;
        existing.AccessToken = accessToken;
        existing.TokenExpiresAtUnix = expiresUnix;

        if (config.IsPrimaryUser(jellyfinUser) || config.IsPrimaryUser(jellyfinUserId))
        {
            config.PersistedRefreshToken = refreshToken;
            config.PersistedAccessToken = accessToken;
            config.PersistedTokenExpiresAtUnix = expiresUnix;
        }

        Plugin.Instance?.SaveConfiguration();
        _cache.Clear();
        return (true, null, existing);
    }

    public async Task<string?> EnsureUserAccessTokenAsync(string? usernameOrId, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var account = config.GetAccountForUser(usernameOrId);
        if (account == null || string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            return null;
        }

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (account.TokenExpiresAtUnix > nowUnix + 60)
        {
            return string.IsNullOrWhiteSpace(account.AccessToken) ? null : account.AccessToken;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (account.TokenExpiresAtUnix > nowUnix + 60)
            {
                return string.IsNullOrWhiteSpace(account.AccessToken) ? null : account.AccessToken;
            }

            var clientId = !string.IsNullOrWhiteSpace(account.ClientId)
                ? account.ClientId
                : (!string.IsNullOrWhiteSpace(config.OAuthClientId) ? config.OAuthClientId : DefaultTvClientId);
            var clientSecret = !string.IsNullOrWhiteSpace(account.ClientSecret)
                ? account.ClientSecret
                : (!string.IsNullOrWhiteSpace(config.OAuthClientSecret) ? config.OAuthClientSecret : DefaultTvClientSecret);

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = account.RefreshToken,
                ["grant_type"] = "refresh_token"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(form)
            };

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("YouTube OAuth token refresh failed with HTTP {Status} for user {User}; backing off for 10m", resp.StatusCode, usernameOrId);
                account.AccessToken = string.Empty;
                account.TokenExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600;
                if (config.IsPrimaryUser(usernameOrId))
                {
                    config.PersistedAccessToken = string.Empty;
                    config.PersistedTokenExpiresAtUnix = account.TokenExpiresAtUnix;
                }

                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var atProp))
            {
                var newToken = atProp.GetString() ?? string.Empty;
                var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt64() : 3600;
                account.AccessToken = newToken;
                account.TokenExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn - 60;

                if (config.IsPrimaryUser(usernameOrId))
                {
                    config.PersistedAccessToken = newToken;
                    config.PersistedTokenExpiresAtUnix = account.TokenExpiresAtUnix;
                }

                try
                {
                    Plugin.Instance?.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not persist refreshed YouTube token");
                }

                return newToken;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing YouTube OAuth token for {User}", usernameOrId);
            account.AccessToken = string.Empty;
            account.TokenExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120;
        }
        finally
        {
            _authLock.Release();
        }

        return null;
    }

    private async Task<string?> FetchAccountNameAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var snippet = items[0].GetProperty("snippet");
                    return snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch YouTube channel title");
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }

            _disposed = true;
        }
    }
}
