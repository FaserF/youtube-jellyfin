using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Thumbnail metadata from yt-dlp .info.json.
/// </summary>
public class YTDLThumbnail
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>
/// JSON data structure matching yt-dlp / youtube-dl .info.json exports.
/// </summary>
public class YTDLData
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("uploader")]
    public string? Uploader { get; set; }

    [JsonPropertyName("uploader_id")]
    public string? UploaderId { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; set; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("release_timestamp")]
    public long? ReleaseTimestamp { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; set; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; set; }

    [JsonPropertyName("dislike_count")]
    public long? DislikeCount { get; set; }

    [JsonPropertyName("track")]
    public string? Track { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("genre")]
    public string? Genre { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("thumbnails")]
    public List<YTDLThumbnail>? Thumbnails { get; set; }

    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    [JsonPropertyName("series")]
    public string? Series { get; set; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }
}
