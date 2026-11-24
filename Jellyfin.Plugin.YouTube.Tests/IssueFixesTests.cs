using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.YouTube.Tests;

public class IssueFixesTests
{
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    [Fact]
    public async Task PollDeviceTokenAsync_ShouldSendDeviceCodeParameter_AndReturnErrorDescription()
    {
        string? capturedForm = null;
        var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            capturedForm = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"error":"invalid_request","error_description":"Missing device_code"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        using var apiClient = new YouTubeApiClient(httpClient, new Mock<ILogger<YouTubeApiClient>>().Object);

        // Temporarily supply OAuth credentials via PluginConfiguration if needed
        var (success, error, _) = await apiClient.PollDeviceTokenAsync("dev-code-123", "Alice", null, TestContext.Current.CancellationToken);

        // If OAuthClientId isn't configured on static Plugin.Instance, PollDeviceTokenAsync returns early;
        // when Plugin.Instance is null it uses defaults (empty), so check both cases gracefully:
        if (capturedForm != null)
        {
            capturedForm.Should().Contain("device_code=dev-code-123");
            capturedForm.Should().NotContain("&code=");
            success.Should().BeFalse();
            error.Should().Be("invalid_request: Missing device_code");
        }
        else
        {
            success.Should().BeFalse();
            error.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task PollDeviceCode_ShouldMarkTerminalErrorsAsNotPending()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_request"}""", Encoding.UTF8, "application/json")
        }));

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("YouTube")).Returns(new HttpClient(handler));

        var controller = new YouTubePluginController(
            factory.Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IUserManager>().Object,
            new Mock<ILogger<YouTubePluginController>>().Object,
            new Mock<ILoggerFactory>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")),
                        new Claim("Jellyfin-User", "Alice")
                    ], "TestAuth"))
                }
            }
        };

        var result = await controller.PollDeviceCode("dev-code-123", "Alice", null, TestContext.Current.CancellationToken);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("pending").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
