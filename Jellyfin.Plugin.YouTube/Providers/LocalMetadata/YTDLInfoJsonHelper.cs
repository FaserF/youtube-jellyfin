using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.YouTube.Providers.LocalMetadata;

/// <summary>
/// Helper utilities to find, parse, and map local yt-dlp .info.json metadata.
/// </summary>
public static class YTDLInfoJsonHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string? FindInfoJsonPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        // 1. Exact match with extension swapped: video.mkv -> video.info.json
        var direct = Path.ChangeExtension(mediaPath, ".info.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        // 2. Suffix match: video.mkv -> video.mkv.info.json
        var appended = mediaPath + ".info.json";
        if (File.Exists(appended))
        {
            return appended;
        }

        // 3. Search directory for matching YouTube ID if file has an ID
        var dir = Path.GetDirectoryName(mediaPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            var vid = YouTubeIdKeys.ExtractVideoIdFromPath(mediaPath);
            if (!string.IsNullOrWhiteSpace(vid))
            {
                try
                {
                    var matches = Directory.GetFiles(dir, $"*{vid}*.info.json");
                    if (matches.Length > 0)
                    {
                        return matches[0];
                    }
                }
                catch
                {
                    // Ignore transient filesystem access errors
                }
            }
        }

        return null;
    }

    public static YTDLData? ReadInfoJson(string jsonPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var jsonText = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<YTDLData>(jsonText, JsonOptions);
    }

    public static DateTime? ParseDate(YTDLData data)
    {
        if (!string.IsNullOrWhiteSpace(data.UploadDate))
        {
            if (DateTime.TryParseExact(data.UploadDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            {
                return d1;
            }
            if (DateTime.TryParse(data.UploadDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            {
                return d2;
            }
        }

        if (data.Timestamp.HasValue && data.Timestamp.Value > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(data.Timestamp.Value).UtcDateTime;
        }

        if (!string.IsNullOrWhiteSpace(data.ReleaseDate) &&
            DateTime.TryParse(data.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d3))
        {
            return d3;
        }

        if (data.ReleaseTimestamp.HasValue && data.ReleaseTimestamp.Value > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(data.ReleaseTimestamp.Value).UtcDateTime;
        }

        return null;
    }

    public static float CalculateRating(YTDLData data)
    {
        var likes = data.LikeCount ?? 0;
        var dislikes = data.DislikeCount ?? 0;
        var total = likes + dislikes;
        if (total > 0)
        {
            var r = (float)Math.Round((double)likes / total * 10.0, 1);
            return Math.Clamp(r, 0f, 10f);
        }

        return 8.0f;
    }

    public static void PopulateEpisode(Episode episode, YTDLData data)
    {
        var title = !string.IsNullOrWhiteSpace(data.Title) ? data.Title : episode.Name;
        episode.Name = title;
        episode.Overview = data.Description ?? string.Empty;

        var date = ParseDate(data);
        if (date.HasValue)
        {
            episode.PremiereDate = date.Value;
            episode.ProductionYear = date.Value.Year;
            episode.ForcedSortName = $"{date.Value:yyyyMMdd}-{title}";
        }

        var channel = data.Uploader ?? data.Channel;
        if (!string.IsNullOrWhiteSpace(channel))
        {
            episode.AddStudio(channel);
        }

        if (data.Categories != null)
        {
            foreach (var cat in data.Categories)
            {
                episode.AddGenre(cat);
            }
        }

        episode.AddGenre("YouTube");

        if (data.Tags != null && data.Tags.Count > 0)
        {
            episode.Tags = data.Tags.Take(10).ToArray();
        }

        episode.CommunityRating = CalculateRating(data);

        if (data.EpisodeNumber.HasValue && data.EpisodeNumber.Value > 0)
        {
            episode.IndexNumber = data.EpisodeNumber.Value;
        }

        if (data.SeasonNumber.HasValue && data.SeasonNumber.Value > 0)
        {
            episode.ParentIndexNumber = data.SeasonNumber.Value;
        }

        if (!string.IsNullOrWhiteSpace(data.Id))
        {
            YouTubeIdKeys.SetVideoId(episode, data.Id);
        }
    }

    public static void PopulateMovie(Movie movie, YTDLData data)
    {
        var title = !string.IsNullOrWhiteSpace(data.Title) ? data.Title : movie.Name;
        movie.Name = title;
        movie.Overview = data.Description ?? string.Empty;

        var date = ParseDate(data);
        if (date.HasValue)
        {
            movie.PremiereDate = date.Value;
            movie.ProductionYear = date.Value.Year;
        }

        var channel = data.Uploader ?? data.Channel;
        if (!string.IsNullOrWhiteSpace(channel))
        {
            movie.AddStudio(channel);
        }

        if (data.Categories != null)
        {
            foreach (var cat in data.Categories)
            {
                movie.AddGenre(cat);
            }
        }

        movie.AddGenre("YouTube");

        if (data.Tags != null && data.Tags.Count > 0)
        {
            movie.Tags = data.Tags.Take(10).ToArray();
        }

        movie.CommunityRating = CalculateRating(data);

        if (!string.IsNullOrWhiteSpace(data.Id))
        {
            YouTubeIdKeys.SetMovieId(movie, data.Id);
        }
    }

    public static void PopulateMusicVideo(MusicVideo musicVideo, YTDLData data)
    {
        var title = !string.IsNullOrWhiteSpace(data.Track)
            ? data.Track
            : (!string.IsNullOrWhiteSpace(data.Title) ? data.Title : musicVideo.Name);
        musicVideo.Name = title;
        musicVideo.Overview = data.Description ?? string.Empty;

        var artist = data.Artist ?? data.Uploader ?? data.Channel;
        if (!string.IsNullOrWhiteSpace(artist))
        {
            musicVideo.Artists = new[] { artist };
            musicVideo.AddStudio(artist);
        }

        if (!string.IsNullOrWhiteSpace(data.Album))
        {
            musicVideo.Album = data.Album;
        }

        var date = ParseDate(data);
        if (date.HasValue)
        {
            musicVideo.PremiereDate = date.Value;
            musicVideo.ProductionYear = date.Value.Year;
        }

        if (data.Categories != null)
        {
            foreach (var cat in data.Categories)
            {
                musicVideo.AddGenre(cat);
            }
        }

        musicVideo.AddGenre("YouTube");
        musicVideo.AddGenre("Music Video");

        if (data.Tags != null && data.Tags.Count > 0)
        {
            musicVideo.Tags = data.Tags.Take(10).ToArray();
        }

        musicVideo.CommunityRating = CalculateRating(data);

        if (!string.IsNullOrWhiteSpace(data.Id))
        {
            YouTubeIdKeys.SetMusicVideoId(musicVideo, data.Id);
        }
    }

    public static void PopulateSeries(Series series, YTDLData data)
    {
        var name = data.Channel ?? data.Uploader;
        if (!string.IsNullOrWhiteSpace(name))
        {
            series.Name = name;
        }

        if (!string.IsNullOrWhiteSpace(data.Description) && string.IsNullOrWhiteSpace(series.Overview))
        {
            series.Overview = data.Description;
        }

        var channelId = data.ChannelId ?? data.UploaderId;
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            YouTubeIdKeys.SetChannelId(series, channelId);
        }
    }

    public static void PopulateSeason(Season season, YTDLData data)
    {
        if (data.SeasonNumber.HasValue && data.SeasonNumber.Value > 0)
        {
            season.IndexNumber = data.SeasonNumber.Value;
        }
    }

    public static List<string> FindLocalImageFiles(string? mediaPath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return results;
        }

        var extensions = new[] { ".jpg", ".webp", ".png", ".jpeg" };
        var baseWithoutExt = Path.ChangeExtension(mediaPath, null);

        // 1. Same base name: video.jpg, video.webp, video.png
        foreach (var ext in extensions)
        {
            var p = baseWithoutExt + ext;
            if (File.Exists(p))
            {
                results.Add(p);
            }
        }

        // 2. Poster / Thumb suffixes
        var suffixes = new[] { "-thumb", "-poster", "-fanart", "_thumb" };
        foreach (var suf in suffixes)
        {
            foreach (var ext in extensions)
            {
                var p = baseWithoutExt + suf + ext;
                if (File.Exists(p))
                {
                    results.Add(p);
                }
            }
        }

        // 3. Search directory by video ID if not found
        if (results.Count == 0)
        {
            var dir = Path.GetDirectoryName(mediaPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var vid = YouTubeIdKeys.ExtractVideoIdFromPath(mediaPath);
                if (!string.IsNullOrWhiteSpace(vid))
                {
                    try
                    {
                        foreach (var ext in extensions)
                        {
                            var matches = Directory.GetFiles(dir, $"*{vid}*{ext}");
                            results.AddRange(matches);
                        }
                    }
                    catch
                    {
                        // Ignore directory scan errors
                    }
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
