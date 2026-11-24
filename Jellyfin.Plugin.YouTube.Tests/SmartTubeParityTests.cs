using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Models;
using Xunit;

namespace Jellyfin.Plugin.YouTube.Tests;

public class SmartTubeParityTests
{
    [Fact]
    public void PluginConfiguration_ShouldEnableSmartTubeNativeDefaults()
    {
        var config = new PluginConfiguration();
        config.EnableDeArrow.Should().BeTrue();
        config.DeArrowPreserveOriginalTitle.Should().BeTrue();
        config.HideYouTubeShorts.Should().BeTrue();
        config.BlockedChannels.Should().BeEmpty();
        config.EnableMostReplayedChapter.Should().BeTrue();
    }

    [Fact]
    public void CleanDeArrowTitle_ShouldStripCasualFormattingMarkers()
    {
        YouTubeApiClient.CleanDeArrowTitle(">How >AI >Works in 2026").Should().Be("How AI Works in 2026");
        YouTubeApiClient.CleanDeArrowTitle("  Normal Title  ").Should().Be("Normal Title");
    }

    [Fact]
    public void FilterVideos_ShouldExcludeShortsAndBlockedChannels()
    {
        var config = new PluginConfiguration
        {
            HideYouTubeShorts = true,
            BlockedChannels = "ClickbaitChannel, UCblocked123"
        };

        var videos = new List<YouTubeVideoItem>
        {
            new() { VideoId = "v1", Title = "Deep Dive", ChannelId = "UCgood", ChannelTitle = "TechChannel", DurationSeconds = 600 },
            new() { VideoId = "v2", Title = "Quick Clip", ChannelId = "UCgood", ChannelTitle = "TechChannel", DurationSeconds = 45 },
            new() { VideoId = "v3", Title = "Funny #shorts", ChannelId = "UCgood", ChannelTitle = "TechChannel", DurationSeconds = 0 },
            new() { VideoId = "v4", Title = "Long Video", ChannelId = "UCother", ChannelTitle = "ClickbaitChannel", DurationSeconds = 900 },
            new() { VideoId = "v5", Title = "Another Video", ChannelId = "UCblocked123", ChannelTitle = "Other", DurationSeconds = 900 }
        };

        var filtered = YouTubeApiClient.FilterVideos(videos, config);
        filtered.Should().ContainSingle();
        filtered[0].VideoId.Should().Be("v1");
    }

    [Fact]
    public void ParseHeatmapPeak_ShouldReturnHighestIntensitySegment()
    {
        const string json = """
        {
          "heatmap": [
            { "start_time": 10.0, "end_time": 20.0, "value": 0.45 },
            { "start_time": 120.5, "end_time": 135.0, "value": 0.96 },
            { "start_time": 200.0, "end_time": 210.0, "value": 0.72 }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var peak = YouTubeApiClient.ParseHeatmapPeak(doc.RootElement);

        peak.Should().NotBeNull();
        peak!.StartSeconds.Should().Be(120.5);
        peak.EndSeconds.Should().Be(135.0);
        peak.Intensity.Should().Be(0.96);
    }

    [Fact]
    public async Task TryResolveViaInnertubeAsync_ShouldParseHlsFormatsAndCaptions()
    {
        const string playerJson = """
        {
          "playabilityStatus": { "status": "OK" },
          "videoDetails": {
            "videoId": "dQw4w9WgXcQ",
            "title": "Test Video",
            "author": "Test Channel",
            "channelId": "UCtest",
            "lengthSeconds": "212",
            "shortDescription": "Test desc"
          },
          "streamingData": {
            "hlsManifestUrl": "https://manifest.googlevideo.com/api/manifest/hls_variant/test.m3u8",
            "formats": [
              { "url": "https://rr1.googlevideo.com/videoplayback?itag=18", "height": 360, "width": 640, "bitrate": 500000 }
            ],
            "adaptiveFormats": [
              { "url": "https://rr1.googlevideo.com/videoplayback?itag=140", "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"", "bitrate": 128000 }
            ]
          },
          "captions": {
            "playerCaptionsTracklistRenderer": {
              "captionTracks": [
                { "languageCode": "en", "baseUrl": "https://www.youtube.com/api/timedtext?v=dQw4w9WgXcQ&lang=en" }
              ]
            }
          }
        }
        """;

        var handler = new StubHandler(playerJson);
        using var http = new HttpClient(handler);
        using var client = new YouTubeApiClient(http, new Moq.Mock<Microsoft.Extensions.Logging.ILogger<YouTubeApiClient>>().Object);

        var info = await client.TryResolveViaInnertubeAsync("dQw4w9WgXcQ", new PluginConfiguration(), TestContext.Current.CancellationToken);
        info.Should().NotBeNull();
        info!.HlsManifestUrl.Should().Contain("manifest.googlevideo.com");
        info.DirectStreamUrl.Should().Contain("googlevideo.com");
        info.AudioStreamUrl.Should().Contain("itag=140");
        info.Title.Should().Be("Test Video");
        info.DurationSeconds.Should().Be(212);
        info.Subtitles.Should().ContainKey("en");
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
