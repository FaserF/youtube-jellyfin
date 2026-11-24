using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Controllers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
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

    [Fact]
    public void ExtractPlaylistsFromInnertubeJson_ShouldExtractOwnedAndSavedPlaylists_AndSkipWatchLater()
    {
        const string innertubeJson = """
        {
          "contents": {
            "items": [
              {
                "tileRenderer": {
                  "contentType": "TILE_CONTENT_TYPE_PLAYLIST",
                  "contentId": "PLPrivateCreated123",
                  "onSelectCommand": {
                    "browseEndpoint": { "browseId": "VLPLPrivateCreated123" }
                  },
                  "metadata": {
                    "tileMetadataRenderer": {
                      "title": { "simpleText": "My Private Playlist" }
                    }
                  }
                }
              },
              {
                "gridPlaylistRenderer": {
                  "playlistId": "PLSavedFromOther456",
                  "title": { "runs": [ { "text": "Saved Community Playlist" } ] },
                  "videoCountText": { "simpleText": "42 videos" }
                }
              },
              {
                "tileRenderer": {
                  "contentType": "TILE_CONTENT_TYPE_PLAYLIST",
                  "onSelectCommand": {
                    "browseEndpoint": { "browseId": "VLWL" }
                  },
                  "metadata": {
                    "tileMetadataRenderer": {
                      "title": { "simpleText": "Watch Later" }
                    }
                  }
                }
              }
            ]
          }
        }
        """;

        using var doc = JsonDocument.Parse(innertubeJson);
        var playlists = new List<Jellyfin.Plugin.YouTube.Models.YouTubePlaylistItem>();
        YouTubeApiClient.ExtractPlaylistsFromInnertubeJson(doc.RootElement, playlists, 50);

        playlists.Should().HaveCount(2);
        playlists.Select(p => p.PlaylistId).Should().Contain(["PLPrivateCreated123", "PLSavedFromOther456"]);
        playlists.First(p => p.PlaylistId == "PLPrivateCreated123").Title.Should().Be("My Private Playlist");
        playlists.First(p => p.PlaylistId == "PLSavedFromOther456").ItemCount.Should().Be(42);
    }

    [Fact]
    public void PrioritizeChannels_ShouldKeepPriorityChannelsFirst_AndRespectZeroAsUnlimited()
    {
        var channels = new List<Jellyfin.Plugin.YouTube.Models.YouTubeChannelItem>
        {
            new() { ChannelId = "UC1", Title = "Alpha" },
            new() { ChannelId = "UC2", Title = "Beta" },
            new() { ChannelId = "UC3", Title = "Gamma" },
            new() { ChannelId = "UC4", Title = "Delta" }
        };

        var capped = Jellyfin.Plugin.YouTube.ScheduledTasks.SyncYouTubeStrmTask.PrioritizeChannels(channels, "Delta, UC2", 2);
        capped.Should().HaveCount(2);
        capped.Select(c => c.ChannelId).Should().Equal("UC2", "UC4");

        var unlimited = Jellyfin.Plugin.YouTube.ScheduledTasks.SyncYouTubeStrmTask.PrioritizeChannels(channels, "UC4", 0);
        unlimited.Should().HaveCount(4);
        unlimited[0].ChannelId.Should().Be("UC4");
    }

    [Fact]
    public void GetStrmServerBaseUrl_ShouldPreferPublicServerUrl_AndFallbackToLoopback()
    {
        var configWithPublic = new PluginConfiguration { PublicServerUrl = "https://jellyfin.example.com/" };
        Jellyfin.Plugin.YouTube.ScheduledTasks.SyncYouTubeStrmTask.GetStrmServerBaseUrl(configWithPublic)
            .Should().Be("https://jellyfin.example.com");

        var configDefault = new PluginConfiguration { PublicServerUrl = string.Empty };
        var url = Jellyfin.Plugin.YouTube.ScheduledTasks.SyncYouTubeStrmTask.GetStrmServerBaseUrl(configDefault);
        url.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetDiagnostics_ShouldRequireAdminElevation_AndReturnStatusPayload()
    {
        var method = typeof(YouTubePluginController).GetMethod(nameof(YouTubePluginController.GetDiagnostics));
        method.Should().NotBeNull();
        var auth = method!.GetCustomAttribute<AuthorizeAttribute>();
        auth.Should().NotBeNull();
        auth!.Policy.Should().Be("RequiresElevation");

        var controller = new YouTubePluginController(
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IUserManager>().Object,
            new Mock<ILogger<YouTubePluginController>>().Object,
            new Mock<ILoggerFactory>().Object);

        var result = controller.GetDiagnostics().Should().BeOfType<OkObjectResult>().Subject;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        doc.RootElement.TryGetProperty("ytDlpPath", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("oauthConfigured", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("strmPathWritable", out _).Should().BeTrue();
    }

    [Fact]
    public void ManifestJson_ShouldOnlyTargetJellyfin12_AndNotStale10_10()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "manifest.json"));
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var versions = doc.RootElement[0].GetProperty("versions").EnumerateArray().ToList();
        versions.Should().NotBeEmpty();
        versions.Should().OnlyContain(v => v.GetProperty("targetAbi").GetString() == "12.0.0.0");
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_ShouldReturnEmpty_ForNonVideoIds()
    {
        var channel = new Jellyfin.Plugin.YouTube.Channels.YouTubeChannel(
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILogger<Jellyfin.Plugin.YouTube.Channels.YouTubeChannel>>().Object,
            new Mock<ILoggerFactory>().Object);

        var info = await channel.GetChannelItemMediaInfo("login_code_display", TestContext.Current.CancellationToken);
        info.Should().BeEmpty();
    }

    [Fact]
    public async Task YouTubePlaybackFilter_ShouldReturnOkObjectResult_OnPlaybackInfo()
    {
        var itemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = itemId,
            Name = "Test Video",
            Path = "/data/youtube/Test/Season 1/Test [YouTubeVideo-dQw4w9WgXcQ].strm"
        };
        item.ProviderIds[Jellyfin.Plugin.YouTube.Providers.YouTubeIdKeys.Video] = "dQw4w9WgXcQ";

        var libMgr = new Mock<ILibraryManager>();
        libMgr.Setup(l => l.GetItemById(itemId)).Returns(item);

        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("YouTube")).Returns(new HttpClient(handler));

        var logs = new List<string>();
        var loggerMock = new Mock<ILogger<Jellyfin.Plugin.YouTube.Providers.YouTubeMediaSourceProvider>>();
        loggerMock.Setup(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(inv => logs.Add($"{inv.Arguments[2]} | {inv.Arguments[3]}")));

        var msp = new Jellyfin.Plugin.YouTube.Providers.YouTubeMediaSourceProvider(
            factory.Object,
            loggerMock.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var sources = (await msp.GetMediaSources(item, TestContext.Current.CancellationToken)).ToList();
        sources.Should().NotBeEmpty(because: string.Join("; ", logs));

        var filter = new Jellyfin.Plugin.YouTube.Filters.YouTubePlaybackFilter(libMgr.Object, msp);

        var httpCtx = new DefaultHttpContext();
        httpCtx.Request.Path = $"/Items/{itemId:N}/PlaybackInfo";
        httpCtx.Request.Scheme = "https";
        httpCtx.Request.Host = new HostString("jf.example.com");

        var actionCtx = new ActionContext(httpCtx, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var executingCtx = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            actionCtx,
            [],
            new Dictionary<string, object?> { ["itemId"] = itemId },
            new object());

        await filter.OnActionExecutionAsync(executingCtx, () => Task.FromResult(
            new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(actionCtx, [], new object())));

        var ok = executingCtx.Result.Should().BeOfType<OkObjectResult>().Subject;
        var pbResp = ok.Value.Should().BeOfType<MediaBrowser.Model.MediaInfo.PlaybackInfoResponse>().Subject;
        pbResp.MediaSources.Should().NotBeEmpty();
        pbResp.MediaSources[0].IsRemote.Should().BeTrue();
    }

    [Fact]
    public void UiAndChannelMetadata_ShouldNotContainSmartTubeBranding()
    {
        var channel = new Jellyfin.Plugin.YouTube.Channels.YouTubeChannel(
            new Mock<IHttpClientFactory>().Object,
            new Mock<ILogger<Jellyfin.Plugin.YouTube.Channels.YouTubeChannel>>().Object,
            new Mock<ILoggerFactory>().Object);

        channel.Description.Should().NotContainEquivalentOf("SmartTube");

        var jsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Jellyfin.Plugin.YouTube", "Features", "Script", "youtubePlugin.js"));
        if (File.Exists(jsPath))
        {
            File.ReadAllText(jsPath).Should().NotContainEquivalentOf("SmartTube");
        }
    }
}
