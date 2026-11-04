using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.YouTube.Models;

public class YouTubeVideoItem
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelTitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public long DurationSeconds { get; set; }
    public long ViewCount { get; set; }
    public string PublishedTimeText { get; set; } = string.Empty;
    public DateTime? PublishedAt { get; set; }
    public int ResumePercent { get; set; }
    public bool IsLive { get; set; }
    public bool IsShort { get; set; }
    public ReturnYouTubeDislikeDto? DislikeStats { get; set; }
    public List<SponsorBlockSegmentDto> SponsorSegments { get; set; } = new();
}

public class YouTubeChannelItem
{
    public string ChannelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public string SubscriberCountText { get; set; } = string.Empty;
}

public class YouTubePlaylistItem
{
    public string PlaylistId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public int ItemCount { get; set; }
}

public class YouTubeStreamInfoDto
{
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ChannelTitle { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long DurationSeconds { get; set; }
    public string HlsManifestUrl { get; set; } = string.Empty;
    public string DirectStreamUrl { get; set; } = string.Empty;
    public string AudioStreamUrl { get; set; } = string.Empty;
    public string VideoCodec { get; set; } = "h264";
    public string AudioCodec { get; set; } = "aac";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;

    /// <summary>Language code yt-dlp detected as the native video language (e.g. "de", "en").</summary>
    public string? NativeLanguage { get; set; }

    /// <summary>All distinct available resolutions (heights) sorted descending (e.g. 2160, 1080, 720, 480, 360).</summary>
    public List<YouTubeVideoFormatDto> VideoFormats { get; set; } = new();

    public Dictionary<string, string> Subtitles { get; set; } = new();
    public List<YouTubeSubtitleTrackDto> SubtitleTracks { get; set; } = new();
    public List<YouTubeAudioTrackDto> AudioTracks { get; set; } = new();
    public List<SponsorBlockSegmentDto> SponsorSegments { get; set; } = new();
    public List<YouTubeChapterDto> Chapters { get; set; } = new();
    public ReturnYouTubeDislikeDto? DislikeStats { get; set; }

    /// <summary>YouTube video tags (used as Jellyfin tags/genres).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>YouTube video category (e.g. "Education", "Entertainment").</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Actual publish date from yt-dlp (upload_date field).</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Like percentage 0-100 derived from RYD (used as CommunityRating * 10).</summary>
    public float LikePercent { get; set; }
}

public class YouTubeVideoFormatDto
{
    public int Height { get; set; }
    public int Width { get; set; }
    public string Codec { get; set; } = "h264";
    public long Bitrate { get; set; }
    public float Fps { get; set; }
    public bool IsHdr { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string FormatId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty; // "1080p HD", "2160p 4K HDR"
}

public class YouTubeChapterDto
{
    public string Title { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}

public class YouTubeAudioTrackDto
{
    public string FormatId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Codec { get; set; } = "aac";
    public int Bitrate { get; set; } = 128000;
    public int Channels { get; set; } = 2;
    public int SampleRate { get; set; } = 44100;
    public bool IsOriginal { get; set; }
    public bool IsDubbed { get; set; }
    public bool IsDefault { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
}

public class YouTubeSubtitleTrackDto
{
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Format { get; set; } = "vtt";
    public bool IsAutoGenerated { get; set; }
    public bool IsDefault { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
}

public class SponsorBlockSegmentDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = "skip";

    [JsonPropertyName("segment")]
    public double[] Segment { get; set; } = Array.Empty<double>();

    [JsonPropertyName("UUID")]
    public string Uuid { get; set; } = string.Empty;

    public double StartSeconds => Segment.Length > 0 ? Segment[0] : 0;
    public double EndSeconds => Segment.Length > 1 ? Segment[1] : 0;
}

public class ReturnYouTubeDislikeDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("likes")]
    public long Likes { get; set; }

    [JsonPropertyName("dislikes")]
    public long Dislikes { get; set; }

    [JsonPropertyName("rating")]
    public double Rating { get; set; }

    [JsonPropertyName("viewCount")]
    public long ViewCount { get; set; }
}

public class YouTubeDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = "https://www.youtube.com/tv/activate";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; } = 1800;

    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 5;
}

public class YouTubeCommentDto
{
    public string Author { get; set; } = string.Empty;
    public string AuthorAvatarUrl { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string PublishedTimeText { get; set; } = string.Empty;
    public string LikeCountText { get; set; } = string.Empty;
}
