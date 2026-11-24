using System.Net;
using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Jellyfin.Plugin.YouTube.Controllers;
using Jellyfin.Plugin.YouTube.Helpers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.YouTube.Tests;

public class SecurityHardeningTests
{
    [Theory]
    [InlineData("googlevideo.com", true)]
    [InlineData("rr1---sn-4g5e6nzz.googlevideo.com", true)]
    [InlineData("manifest.googlevideo.com", true)]
    [InlineData("i.ytimg.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("yt3.ggpht.com", true)]
    [InlineData("lh3.googleusercontent.com", true)]
    [InlineData("notgooglevideo.com", false)]
    [InlineData("googlevideo.com.evil.com", false)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("", false)]
    public void IsAllowedYouTubeCdnHost_ShouldEnforceStrictAllowlist(string host, bool expected)
    {
        ProxySecurityHelper.IsAllowedYouTubeCdnHost(host).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://rr1---sn-4g5e6nzz.googlevideo.com/videoplayback?id=1", true)]
    [InlineData("http://rr1---sn-4g5e6nzz.googlevideo.com/videoplayback?id=1", false)]
    [InlineData("https://user:pass@rr1---sn-4g5e6nzz.googlevideo.com/videoplayback", false)]
    [InlineData("https://rr1---sn-4g5e6nzz.googlevideo.com:8443/videoplayback", false)]
    [InlineData("https://127.0.0.1/secret", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://internal.corp.local/api", false)]
    public void TryValidateTargetUri_ShouldRejectUnsafeUrls(string url, bool expected)
    {
        ProxySecurityHelper.TryValidateTargetUri(url, out _, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.100", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("142.250.185.78", false)]
    [InlineData("2a00:1450:4001:828::200e", false)]
    public void IsBlockedIpAddress_ShouldBlockNonPublicRanges(string ipString, bool expectedBlocked)
    {
        var ip = IPAddress.Parse(ipString);
        ProxySecurityHelper.IsBlockedIpAddress(ip).Should().Be(expectedBlocked);
    }

    [Fact]
    public void ProxySignature_ShouldVerifyValidAndRejectTampered()
    {
        const string target = "https://rr1---sn-4g5e6nzz.googlevideo.com/videoplayback?expire=123";
        var sig = ProxySecurityHelper.ComputeSignature(target);

        ProxySecurityHelper.VerifySignature(target, sig).Should().BeTrue();
        ProxySecurityHelper.VerifySignature(target + "&extra=1", sig).Should().BeFalse();
        ProxySecurityHelper.VerifySignature(target, "invalid_sig").Should().BeFalse();
        ProxySecurityHelper.VerifySignature(target, null).Should().BeFalse();
    }

    [Fact]
    public void ControllerAuthHelper_ShouldEnforceOwnershipAndAdminRoles()
    {
        var userManager = new Mock<IUserManager>();
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();

        var unauthPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
        ControllerAuthHelper.TryGetCaller(unauthPrincipal, userManager.Object, out _).Should().BeFalse();

        var alicePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("Jellyfin-UserId", aliceId.ToString("N")),
            new Claim("Jellyfin-User", "Alice")
        ], "TestAuth"));

        ControllerAuthHelper.TryGetCaller(alicePrincipal, userManager.Object, out var aliceCaller).Should().BeTrue();
        aliceCaller!.IsAdmin.Should().BeFalse();
        ControllerAuthHelper.IsOwnerOrAdmin(aliceCaller, "Alice").Should().BeTrue();
        ControllerAuthHelper.IsOwnerOrAdmin(aliceCaller, aliceId.ToString("D")).Should().BeTrue();
        ControllerAuthHelper.IsOwnerOrAdmin(aliceCaller, "Bob").Should().BeFalse();
        ControllerAuthHelper.IsOwnerOrAdmin(aliceCaller, bobId.ToString("N")).Should().BeFalse();

        var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("Jellyfin-UserId", aliceId.ToString("N")),
            new Claim("Jellyfin-User", "Alice"),
            new Claim(ClaimTypes.Role, "Administrator")
        ], "TestAuth"));

        ControllerAuthHelper.TryGetCaller(adminPrincipal, userManager.Object, out var adminCaller).Should().BeTrue();
        adminCaller!.IsAdmin.Should().BeTrue();
        ControllerAuthHelper.IsOwnerOrAdmin(adminCaller, "Bob").Should().BeTrue();
    }

    [Fact]
    public void YouTubePluginController_ShouldRequireAuthOnStateChangingAndAdminEndpoints()
    {
        var controllerType = typeof(YouTubePluginController);
        controllerType.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();

        string[] mustNotBeAnonymous =
        [
            nameof(YouTubePluginController.RequestDeviceCode),
            nameof(YouTubePluginController.PollDeviceCode),
            nameof(YouTubePluginController.GetUserMappings),
            nameof(YouTubePluginController.UnlinkUserAccount),
            nameof(YouTubePluginController.RateVideo),
            nameof(YouTubePluginController.Subscribe),
            nameof(YouTubePluginController.SyncLibrary),
            nameof(YouTubePluginController.SyncTrailers),
            nameof(YouTubePluginController.Search)
        ];

        foreach (var methodName in mustNotBeAnonymous)
        {
            var method = controllerType.GetMethod(methodName);
            method.Should().NotBeNull($"Method {methodName} should exist");
            method!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull($"{methodName} must not allow anonymous access");
        }

        var syncLibAuth = controllerType.GetMethod(nameof(YouTubePluginController.SyncLibrary))!.GetCustomAttribute<AuthorizeAttribute>();
        syncLibAuth.Should().NotBeNull();
        syncLibAuth!.Policy.Should().Be("RequiresElevation");

        var syncTrailersAuth = controllerType.GetMethod(nameof(YouTubePluginController.SyncTrailers))!.GetCustomAttribute<AuthorizeAttribute>();
        syncTrailersAuth.Should().NotBeNull();
        syncTrailersAuth!.Policy.Should().Be("RequiresElevation");
    }

    [Fact]
    public async Task ProxyUrl_ShouldRejectUnauthenticatedAndDisallowedTargets()
    {
        var controller = new YouTubePluginController(
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IUserManager>().Object,
            new Mock<ILogger<YouTubePluginController>>().Object,
            new Mock<ILoggerFactory>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity())
                }
            }
        };

        var unauthResult = await controller.ProxyUrl(
            u: "https://rr1---sn-4g5e6nzz.googlevideo.com/videoplayback",
            sig: "bad",
            cancellationToken: TestContext.Current.CancellationToken);
        unauthResult.Should().BeOfType<UnauthorizedResult>();

        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")),
            new Claim("Jellyfin-User", "Alice")
        ], "TestAuth"));

        var blockedHostResult = await controller.ProxyUrl(
            u: "https://169.254.169.254/latest/meta-data",
            cancellationToken: TestContext.Current.CancellationToken);
        blockedHostResult.Should().BeOfType<BadRequestObjectResult>();
    }
}
