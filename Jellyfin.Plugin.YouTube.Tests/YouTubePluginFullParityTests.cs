using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.YouTube.Api;
using Jellyfin.Plugin.YouTube.Configuration;
using Jellyfin.Plugin.YouTube.Helpers;
using Jellyfin.Plugin.YouTube.Models;
using Jellyfin.Plugin.YouTube.Providers;
using Jellyfin.Plugin.YouTube.Providers.LocalMetadata;
using Jellyfin.Plugin.YouTube.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.YouTube.Tests;

public class YouTubePluginFullParityTests
{
    [Fact]
    public void PluginConfiguration_ShouldHaveSmartTubeParityDefaults()
    {
        var config = new PluginConfiguration();

        config.EnableStreamingChannel.Should().BeFalse();
        config.EnablePublicFallbackForUnlinkedUsers.Should().BeTrue();
        config.PreferredLanguage.Should().Be("en-US");
        config.PreferredRegion.Should().Be("US");
        config.PreferredMaxResolution.Should().Be(2160);
        config.EnableSponsorBlock.Should().BeTrue();
        config.SponsorBlockCategories.Should().Contain("sponsor");
        config.EnableReturnYouTubeDislike.Should().BeTrue();
        config.EnablePlayheadSync.Should().BeTrue();
        config.EnableFavoriteToLikeSync.Should().BeTrue();
        config.EnableYouTubeLibrary.Should().BeTrue();
        config.EnableStrmSync.Should().BeTrue();
        config.EnableMovieSupport.Should().BeTrue();
        config.FetchThumbnails.Should().BeTrue();
        config.StrmLibraryPath.Should().Be("/data/youtube");
        config.UserAccounts.Should().NotBeNull();
    }

    [Fact]
    public void PluginConfiguration_ShouldLinkPrimaryAndIsolateUnlinkedUsers()
    {
        var config = new PluginConfiguration
        {
            PrimaryJellyfinUser = "testuser",
            PrimaryJellyfinUserId = "11112222333344445555666677778888",
            PersistedRefreshToken = "test_refresh_123"
        };

        config.IsPrimaryUser("testuser").Should().BeTrue();
        config.IsPrimaryUser("11112222-3333-4444-5555-666677778888").Should().BeTrue();
        config.IsPrimaryUser("OtherUser").Should().BeFalse();

        var primaryAcc = config.GetAccountForUser("testuser");
        primaryAcc.Should().NotBeNull();
        primaryAcc!.RefreshToken.Should().Be("test_refresh_123");

        var otherAcc = config.GetAccountForUser("OtherUser");
        otherAcc.Should().BeNull();
    }

    [Fact]
    public void MultiUserAccounts_ShouldAllowEachJellyfinUserOwnYouTubeAccount()
    {
        var config = new PluginConfiguration();
        config.UserAccounts.Add(new YouTubeUserAccountEntry
        {
            JellyfinUser = "Alice",
            JellyfinUserId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            AccountName = "Alice YT",
            RefreshToken = "rt_alice"
        });
        config.UserAccounts.Add(new YouTubeUserAccountEntry
        {
            JellyfinUser = "Bob",
            JellyfinUserId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            AccountName = "Bob YT",
            RefreshToken = "rt_bob"
        });

        config.GetAccountForUser("Alice")!.RefreshToken.Should().Be("rt_alice");
        config.GetAccountForUser("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")!.RefreshToken.Should().Be("rt_bob");
        config.GetAccountForUser("Charlie").Should().BeNull();
    }

    [Fact]
    public void FormatRichOverview_ShouldIncludeRydDislikesAndSponsorBlockSummary()
    {
        var video = new YouTubeVideoItem
        {
            VideoId = "aK_bLRLn_m8",
            Title = "Depot-Vergleich 2026",
            ChannelTitle = "Finanzfluss",
            Description = "Alle Broker im Test."
        };
        var ryd = new ReturnYouTubeDislikeDto
        {
            Id = "aK_bLRLn_m8",
            Likes = 12500,
            Dislikes = 250,
            ViewCount = 340000
        };
        var sb = new List<SponsorBlockSegmentDto>
        {
            new() { Category = "sponsor", Segment = new[] { 60.0, 95.5 } },
            new() { Category = "selfpromo", Segment = new[] { 300.0, 320.0 } }
        };

        var overview = YouTubeApiClient.FormatRichOverview(video, ryd, sb);
        overview.Should().Contain("Finanzfluss");
        overview.Should().Contain("12.5K");
        overview.Should().Contain("(98%)");
        overview.Should().Contain("SponsorBlock: 2 Segment(s) (sponsor, selfpromo)");
        overview.Should().Contain("Alle Broker im Test.");
    }

    [Fact]
    public void ExtractVideosFromInnertubeJson_ShouldParseTvHtml5TileRendererWithResumePercent()
    {
        const string sampleJson = """
        {
          "tileRenderer": {
            "contentId": "NA8gTa-GFro",
            "header": {
              "tileHeaderRenderer": {
                "thumbnailOverlays": [
                  { "thumbnailOverlayResumePlaybackRenderer": { "percentDurationWatched": 42 } },
                  { "thumbnailOverlayTimeStatusRenderer": { "text": { "simpleText": "8:21" } } }
                ]
              }
            },
            "metadata": {
              "tileMetadataRenderer": {
                "title": { "simpleText": "How My Game Made $2M" },
                "lines": [
                  { "lineRenderer": { "items": [ { "lineItemRenderer": { "text": { "runs": [ { "text": "The Greatest Developer" } ] } } } ] } }
                ]
              }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(sampleJson);
        var list = new List<YouTubeVideoItem>();
        YouTubeApiClient.ExtractVideosFromInnertubeJson(doc.RootElement, list, 10);

        list.Should().HaveCount(1);
        list[0].VideoId.Should().Be("NA8gTa-GFro");
        list[0].Title.Should().Be("How My Game Made $2M");
        list[0].ChannelTitle.Should().Be("The Greatest Developer");
        list[0].DurationSeconds.Should().Be(501);
        list[0].ResumePercent.Should().Be(42);
    }

    [Fact]
    public void YouTubeIdKeys_ShouldExtractVideoChannelAndPlaylistIdsFromStrmPaths()
    {
        const string strmPath = "/data/youtube/Finanzfluss [YouTubeChannel-UCeARcCUiZg79SQQ-2_XNlXQ]/Season 01 [YouTubePlaylist-PL1234567890ABCDEF]/S01E01 - Test [YouTubeVideo-aK_bLRLn_m8].strm";

        YouTubeIdKeys.ExtractVideoIdFromPath(strmPath).Should().Be("aK_bLRLn_m8");
        YouTubeIdKeys.ExtractChannelIdFromPath(strmPath).Should().Be("UCeARcCUiZg79SQQ-2_XNlXQ");
        YouTubeIdKeys.ExtractPlaylistIdFromPath(strmPath).Should().Be("PL1234567890ABCDEF");
    }

    [Fact]
    public void YouTubeExternalUrlProvider_ShouldGenerateCanonicalYouTubeUrls()
    {
        var urlProvider = new YouTubeExternalUrlProvider();

        var series = new Series();
        YouTubeIdKeys.SetChannelId(series, "UCeARcCUiZg79SQQ-2_XNlXQ");
        urlProvider.GetExternalUrls(series).Should().ContainSingle("https://www.youtube.com/channel/UCeARcCUiZg79SQQ-2_XNlXQ");

        var episode = new Episode();
        YouTubeIdKeys.SetVideoId(episode, "aK_bLRLn_m8");
        urlProvider.GetExternalUrls(episode).Should().ContainSingle("https://www.youtube.com/watch?v=aK_bLRLn_m8");

        var movie = new Movie();
        YouTubeIdKeys.SetMovieId(movie, "dQw4w9WgXcQ");
        urlProvider.GetExternalUrls(movie).Should().ContainSingle("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
    }

    [Fact]
    public void SubtitleHelper_ShouldConvertSrv1XmlAndJson3ToWebVtt()
    {
        const string srv1Xml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <transcript>
            <text start="1.5" dur="2.25">Hello &amp; Welcome</text>
            <text start="5.0" dur="3.0">SmartTube Parity</text>
        </transcript>
        """;

        var vttFromXml = SubtitleHelper.ConvertSrv1XmlToWebVtt(srv1Xml);
        vttFromXml.Should().StartWith("WEBVTT");
        vttFromXml.Should().Contain("00:00:01.500 --> 00:00:03.750");
        vttFromXml.Should().Contain("Hello & Welcome");

        const string json3 = """
        {
          "events": [
            {
              "tStartMs": 12000,
              "dDurationMs": 2500,
              "segs": [ { "utf8": "JSON3 Subtitle Line" } ]
            }
          ]
        }
        """;

        var vttFromJson3 = SubtitleHelper.ConvertJson3ToWebVtt(json3);
        vttFromJson3.Should().Contain("00:00:12.000 --> 00:00:14.500");
        vttFromJson3.Should().Contain("JSON3 Subtitle Line");
    }

    [Fact]
    public void SubtitleHelper_ShouldBuildSponsorBlockChaptersSortedByStartTime()
    {
        var segments = new List<SponsorBlockSegmentDto>
        {
            new() { Category = "selfpromo", Segment = new[] { 120.0, 145.0 } },
            new() { Category = "sponsor", Segment = new[] { 30.0, 60.0 } }
        };

        var chapters = SubtitleHelper.BuildSponsorBlockChapters(segments);
        chapters.Should().HaveCount(2);
        chapters[0].Title.Should().Be("[SponsorBlock: sponsor]");
        chapters[0].StartTicks.Should().Be(TimeSpan.FromSeconds(30).Ticks);
        chapters[1].Title.Should().Be("[SponsorBlock: selfpromo]");
    }

    [Fact]
    public void SyncYouTubeStrmTask_SanitizeFileName_ShouldStripInvalidCharacters()
    {
        var sanitized = SyncYouTubeStrmTask.SanitizeFileName("What's New: 4K/60fps? *Test* | Episode #1");
        sanitized.Should().NotContain(":");
        sanitized.Should().NotContain("/");
        sanitized.Should().NotContain("?");
        sanitized.Should().NotContain("*");
        sanitized.Should().NotContain("|");
    }

    [Fact]
    public void PluginConfiguration_ShouldEnableTrailerSupportByDefault()
    {
        var config = new PluginConfiguration();
        config.EnableTrailerSupport.Should().BeTrue();
        config.EnableAutoTrailerStrmSync.Should().BeTrue();
        config.TrailerLanguage.Should().Be("en-US");
        config.HideFromContinueWatching.Should().BeTrue();
    }

    [Fact]
    public void YouTubeIdKeys_IsYouTubeItemAndDto_ShouldDetectYouTubeMedia()
    {
        var episode = new Episode
        {
            Path = "/data/youtube/Channel [YouTubeChannel-UC123]/Season 1/Ep [YouTubeVideo-dQw4w9WgXcQ].strm"
        };
        YouTubeIdKeys.IsYouTubeItem(episode, episode.Path).Should().BeTrue();

        var trailer = new Movie
        {
            Path = "/media/movies/Inception (2010)/Inception (2010)-trailer.strm"
        };
        YouTubeIdKeys.IsYouTubeItem(trailer, trailer.Path).Should().BeTrue();

        var normalMovie = new Movie
        {
            Path = "/media/movies/Avatar (2009)/Avatar (2009).mkv"
        };
        YouTubeIdKeys.IsYouTubeItem(normalMovie, normalMovie.Path).Should().BeFalse();

        var dto = new MediaBrowser.Model.Dto.BaseItemDto
        {
            Path = "/data/youtube-music/Queen - Bohemian Rhapsody [YouTubeAudio-fJ9rUzIMcZQ].strm"
        };
        YouTubeIdKeys.IsYouTubeDto(dto).Should().BeTrue();

        var normalDto = new MediaBrowser.Model.Dto.BaseItemDto
        {
            Path = "/media/music/Queen/A Night at the Opera/04 - Bohemian Rhapsody.flac"
        };
        YouTubeIdKeys.IsYouTubeDto(normalDto).Should().BeFalse();
    }

    [Fact]
    public void YouTubeIdKeys_ShouldExtractVideoIdFromTrailersAndUrls()
    {
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText("https://www.youtube.com/watch?v=dQw4w9WgXcQ").Should().Be("dQw4w9WgXcQ");
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText("https://youtu.be/dQw4w9WgXcQ").Should().Be("dQw4w9WgXcQ");
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText("http://127.0.0.1:8096/api/youtubePlugin/stream/dQw4w9WgXcQ/master.m3u8?isTrailer=true").Should().Be("dQw4w9WgXcQ");
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText("[YouTubeVideo-dQw4w9WgXcQ]").Should().Be("dQw4w9WgXcQ");
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText(null).Should().BeNull();
        YouTubeIdKeys.ExtractVideoIdFromUrlOrText("").Should().BeNull();
    }

    [Fact]
    public void YouTubeIdKeys_ShouldExtractVideoId_FromStandardYtDlpFilenames()
    {
        // Standard bracket format from yt-dlp: %(title)s [%(id)s].%(ext)s
        YouTubeIdKeys.ExtractVideoIdFromPath("/media/youtube/Never Gonna Give You Up [dQw4w9WgXcQ].mp4")
            .Should().Be("dQw4w9WgXcQ");

        // Parentheses format
        YouTubeIdKeys.ExtractVideoIdFromPath("/media/youtube/Never Gonna Give You Up (dQw4w9WgXcQ).mkv")
            .Should().Be("dQw4w9WgXcQ");

        // End-of-filename suffix format
        YouTubeIdKeys.ExtractVideoIdFromPath("/media/youtube/Never Gonna Give You Up-dQw4w9WgXcQ.mp4")
            .Should().Be("dQw4w9WgXcQ");

        // Standard bracket channel format from yt-dlp
        YouTubeIdKeys.ExtractChannelIdFromPath("/media/youtube/Rick Astley [UCuAXFkgsw1L7xaCfnd5JJOw]")
            .Should().Be("UCuAXFkgsw1L7xaCfnd5JJOw");

        // Channel in path should not be misidentified as video ID
        var vid = YouTubeIdKeys.ExtractVideoIdFromPath("/media/youtube/Rick Astley [UCuAXFkgsw1L7xaCfnd5JJOw]/ep.mp4");
        vid.Should().BeNull();
    }

    [Fact]
    public void YTDLInfoJsonHelper_ShouldPopulateMetadataFromYtdlData()
    {
        var ytdl = new YTDLData
        {
            Id = "dQw4w9WgXcQ",
            Title = "Rick Astley - Never Gonna Give You Up",
            Description = "The official video for Never Gonna Give You Up",
            Uploader = "Rick Astley",
            Channel = "RickAstleyVEVO",
            ChannelId = "UCuAXFkgsw1L7xaCfnd5JJOw",
            UploadDate = "20091025",
            LikeCount = 16000000,
            DislikeCount = 400000,
            Categories = new List<string> { "Music" },
            Tags = new List<string> { "Rick Astley", "Pop" },
            Track = "Never Gonna Give You Up",
            Artist = "Rick Astley",
            Album = "Whenever You Need Somebody"
        };

        // Test Episode population
        var ep = new Episode();
        YTDLInfoJsonHelper.PopulateEpisode(ep, ytdl);
        ep.Name.Should().Be("Rick Astley - Never Gonna Give You Up");
        ep.Overview.Should().Contain("Never Gonna Give You Up");
        ep.PremiereDate.Should().HaveValue();
        ep.PremiereDate!.Value.Year.Should().Be(2009);
        ep.ProductionYear.Should().Be(2009);
        ep.ForcedSortName.Should().StartWith("20091025-");
        ep.CommunityRating.Should().BeGreaterThan(9.0f);
        YouTubeIdKeys.GetVideoId(ep.ProviderIds).Should().Be("dQw4w9WgXcQ");

        // Test Movie population
        var movie = new Movie();
        YTDLInfoJsonHelper.PopulateMovie(movie, ytdl);
        movie.Name.Should().Be("Rick Astley - Never Gonna Give You Up");
        movie.ProductionYear.Should().Be(2009);
        YouTubeIdKeys.GetVideoId(movie.ProviderIds).Should().Be("dQw4w9WgXcQ");

        // Test MusicVideo population
        var mv = new MusicVideo();
        YTDLInfoJsonHelper.PopulateMusicVideo(mv, ytdl);
        mv.Name.Should().Be("Never Gonna Give You Up");
        mv.Album.Should().Be("Whenever You Need Somebody");
        mv.Artists.Should().Contain("Rick Astley");
        mv.ProductionYear.Should().Be(2009);
        YouTubeIdKeys.GetVideoId(mv.ProviderIds).Should().Be("dQw4w9WgXcQ");

        // Test Series population
        var series = new Series();
        YTDLInfoJsonHelper.PopulateSeries(series, ytdl);
        series.Name.Should().Be("RickAstleyVEVO");
        YouTubeIdKeys.GetChannelId(series.ProviderIds).Should().Be("UCuAXFkgsw1L7xaCfnd5JJOw");
    }

    [Fact]
    public void YouTubeMusicVideoExternalId_ShouldSupportMusicVideo()
    {
        var extId = new YouTubeMusicVideoExternalId();
        var mv = new MusicVideo();
        var ep = new Episode();

        extId.Supports(mv).Should().BeTrue();
        extId.Supports(ep).Should().BeFalse();
        extId.UrlFormatString.Should().Contain("youtube.com/watch?v={0}");
    }

    [Fact]
    public void PluginConfiguration_ShouldIncludeOfflineMetadataOptions()
    {
        var config = new PluginConfiguration();
        config.EnableLocalMetadataProvider.Should().BeTrue();
        config.EnableMusicVideoSupport.Should().BeTrue();
        config.EnableEpisodeIndexer.Should().BeTrue();
    }

    [Fact]
    public async Task YouTubeMediaSourceProvider_ShouldNeverHijack_PhysicalMovieWithRemoteTrailers()
    {
        var provider = new YouTubeMediaSourceProvider(
            new Moq.Mock<IHttpClientFactory>().Object,
            new Moq.Mock<Microsoft.Extensions.Logging.ILogger<YouTubeMediaSourceProvider>>().Object,
            new Moq.Mock<Microsoft.Extensions.Logging.ILoggerFactory>().Object);

        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Is It Wrong to Try to Pick Up Girls in a Dungeon!: Arrow of the Orion",
            Path = "/data/movies/Is It Wrong to Try to Pick Up Girls in a Dungeon! - Arrow of the Orion (2019)/Is It Wrong to Try to Pick Up Girls in a Dungeon! - Arrow of the Orion (2019).mkv",
            RemoteTrailers = new[]
            {
                new MediaUrl { Url = "plugin://plugin.video.youtube/play/?video_id=1uNSqOdcwQI" }
            }
        };

        var sources = await provider.GetMediaSources(movie, CancellationToken.None);
        sources.Should().BeEmpty();
    }

    [Fact]
    public async Task YouTubeMediaSourceProvider_ShouldNeverHijack_PhysicalEpisodeFile()
    {
        var provider = new YouTubeMediaSourceProvider(
            new Moq.Mock<IHttpClientFactory>().Object,
            new Moq.Mock<Microsoft.Extensions.Logging.ILogger<YouTubeMediaSourceProvider>>().Object,
            new Moq.Mock<Microsoft.Extensions.Logging.ILoggerFactory>().Object);

        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode 1",
            Path = "/data/series/Some Show/Season 01/S01E01.mkv"
        };

        var sources = await provider.GetMediaSources(episode, CancellationToken.None);
        sources.Should().BeEmpty();
    }
}
