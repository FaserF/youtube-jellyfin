using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.YouTube.Helpers;

/// <summary>
/// Provides SSRF protection, YouTube CDN host allowlisting, DNS/IP validation,
/// redirect validation, and HMAC-SHA256 URL signing for the media proxy endpoint.
/// </summary>
public static class ProxySecurityHelper
{
    private const int MaxRedirects = 3;

    private static readonly string[] AllowedDomainSuffixes =
    [
        "googlevideo.com",
        "youtube.com",
        "ytimg.com",
        "ggpht.com",
        "googleusercontent.com"
    ];

    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);

    private static readonly SocketsHttpHandler DirectHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectValidatedAsync
    };

    private static readonly HttpClient DirectClient = new(DirectHandler, disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Computes an HMAC-SHA256 URL-safe signature for a proxy target URL.
    /// </summary>
    public static string ComputeSignature(string targetUrl)
    {
        var bytes = Encoding.UTF8.GetBytes(targetUrl);
        var hash = HMACSHA256.HashData(SigningKey, bytes);
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Verifies an HMAC-SHA256 signature for a proxy target URL in constant time.
    /// </summary>
    public static bool VerifySignature(string targetUrl, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(ComputeSignature(targetUrl));
        var actual = Encoding.UTF8.GetBytes(signature.Trim());
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Checks whether a hostname belongs to the allowlisted YouTube CDN / media domains.
    /// </summary>
    public static bool IsAllowedYouTubeCdnHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (var suffix in AllowedDomainSuffixes)
        {
            if (normalized.Equals(suffix, StringComparison.Ordinal) ||
                normalized.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates that a target URL is a clean HTTPS URL targeting an allowlisted YouTube CDN host.
    /// </summary>
    public static bool TryValidateTargetUri(string? targetUrl, out Uri? uri, out string? error)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            error = "Missing target URL";
            return false;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var parsed))
        {
            error = "Invalid target URL";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Only HTTPS URLs are permitted";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URL userinfo is not permitted";
            return false;
        }

        if (!parsed.IsDefaultPort && parsed.Port != 443)
        {
            error = "Non-standard ports are not permitted";
            return false;
        }

        if (parsed.HostNameType != UriHostNameType.Dns)
        {
            error = "IP literal hosts are not permitted";
            return false;
        }

        if (!IsAllowedYouTubeCdnHost(parsed.DnsSafeHost))
        {
            error = "Target host is not in the YouTube CDN allowlist";
            return false;
        }

        uri = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Returns true if the IP address is loopback, private, link-local, CGNAT, multicast, or unspecified.
    /// </summary>
    public static bool IsBlockedIpAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) ||
            ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.Broadcast) ||
            ip.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 ||                                      // 0.0.0.0/8 (unspecified)
                   b[0] == 10 ||                                     // 10.0.0.0/8 (RFC1918)
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||     // 100.64.0.0/10 (CGNAT / Tailscale)
                   b[0] == 127 ||                                    // 127.0.0.0/8 (loopback)
                   (b[0] == 169 && b[1] == 254) ||                   // 169.254.0.0/16 (link-local / metadata)
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||      // 172.16.0.0/12 (RFC1918)
                   (b[0] == 192 && b[1] == 0 && b[2] == 0) ||        // 192.0.0.0/24 (IETF protocol)
                   (b[0] == 192 && b[1] == 0 && b[2] == 2) ||        // 192.0.2.0/24 (TEST-NET-1)
                   (b[0] == 192 && b[1] == 168) ||                   // 192.168.0.0/16 (RFC1918)
                   (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||    // 198.18.0.0/15 (benchmarking)
                   (b[0] == 198 && b[1] == 51 && b[2] == 100) ||     // 198.51.100.0/24 (TEST-NET-2)
                   (b[0] == 203 && b[1] == 0 && b[2] == 113) ||      // 203.0.113.0/24 (TEST-NET-3)
                   b[0] >= 224;                                      // 224.0.0.0/4 (multicast & reserved)
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
            {
                return true;
            }

            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc; // fc00::/7 (ULA)
        }

        return true;
    }

    /// <summary>
    /// Resolves DNS for a host and ensures all returned IP addresses are public/non-blocked.
    /// </summary>
    public static async Task<IPAddress[]> ResolveAndValidateHostAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new HttpRequestException($"No DNS addresses resolved for host '{host}'.");
        }

        foreach (var addr in addresses)
        {
            if (IsBlockedIpAddress(addr))
            {
                throw new HttpRequestException($"Blocked non-public IP address '{addr}' resolved for host '{host}'.");
            }
        }

        return addresses;
    }

    /// <summary>
    /// Wraps an <see cref="HttpResponseMessage"/> and any transient <see cref="HttpClient"/> that owns it.
    /// </summary>
    public sealed class ValidatedHttpResponse(HttpResponseMessage response, HttpClient? ownedClient) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        public void Dispose()
        {
            Response.Dispose();
            ownedClient?.Dispose();
        }
    }

    /// <summary>
    /// Sends an HTTP request after validating the target URI, DNS resolution, socket IP,
    /// and manually validating any HTTP redirects against the same SSRF rules.
    /// </summary>
    public static async Task<ValidatedHttpResponse> SendValidatedAsync(
        HttpMethod method,
        string targetUrl,
        Action<HttpRequestMessage>? configureRequest,
        string? configuredProxyUrl,
        CancellationToken cancellationToken)
    {
        var currentUrl = targetUrl;
        HttpClient? transientProxyClient = null;
        var client = DirectClient;

        if (!string.IsNullOrWhiteSpace(configuredProxyUrl))
        {
            var proxyHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                Proxy = new WebProxy(configuredProxyUrl),
                UseProxy = true
            };
            transientProxyClient = new HttpClient(proxyHandler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client = transientProxyClient;
        }

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!TryValidateTargetUri(currentUrl, out var uri, out var error) || uri == null)
                {
                    throw new HttpRequestException(error ?? "Invalid target URI.");
                }

                await ResolveAndValidateHostAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);

                using var req = new HttpRequestMessage(method, uri);
                configureRequest?.Invoke(req);

                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var statusCode = (int)resp.StatusCode;
                if (statusCode is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location != null)
                {
                    var nextUri = resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location
                        : new Uri(uri, resp.Headers.Location);
                    resp.Dispose();
                    currentUrl = nextUri.ToString();
                    continue;
                }

                return new ValidatedHttpResponse(resp, transientProxyClient);
            }

            throw new HttpRequestException($"Exceeded maximum redirect limit ({MaxRedirects}).");
        }
        catch
        {
            transientProxyClient?.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAndValidateHostAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        Exception? lastException = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastException = ex;
            }
        }

        throw lastException ?? new HttpRequestException($"Unable to connect to {context.DnsEndPoint.Host}.");
    }
}
