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
}
