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
    public Dictionary<string, string> Subtitles { get; set; } = new();
    public List<SponsorBlockSegmentDto> SponsorSegments { get; set; } = new();
    public ReturnYouTubeDislikeDto? DislikeStats { get; set; }
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
