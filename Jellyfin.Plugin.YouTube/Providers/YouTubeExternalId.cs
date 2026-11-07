using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YouTube.Providers;

public static class YouTubeIdKeys
{
    public const string Video = "YouTubeVideo";
    public const string Channel = "YouTubeChannel";
    public const string Playlist = "YouTubePlaylist";
    public const string Movie = "YouTubeMovie";
    public const string MusicVideo = "YouTubeMusicVideo";
    public const string Audio = "YouTubeAudio";
    public const string Album = "YouTubeAlbum";
    public const string Artist = "YouTubeArtist";

    private static readonly Regex VideoPathRegex = new(@"\[(?:YouTubeVideo|YouTubeAudio|YouTubeMusicVideo)-([a-zA-Z0-9_-]{11})\]", RegexOptions.Compiled);
    private static readonly Regex StandardBracketVideoIdRegex = new(@"\[([a-zA-Z0-9_-]{11})\]", RegexOptions.Compiled);
    private static readonly Regex StandardParenVideoIdRegex = new(@"\(([a-zA-Z0-9_-]{11})\)", RegexOptions.Compiled);
    private static readonly Regex ChannelPathRegex = new(@"\[(?:YouTubeChannel|YouTubeArtist)-(UC[a-zA-Z0-9_-]{20,28})\]", RegexOptions.Compiled);
    private static readonly Regex StandardBracketChannelRegex = new(@"\[(UC[a-zA-Z0-9_-]{20,28})\]", RegexOptions.Compiled);
    private static readonly Regex RawChannelRegex = new(@"(UC[a-zA-Z0-9_-]{20,24})", RegexOptions.Compiled);
    private static readonly Regex PlaylistPathRegex = new(@"\[(?:YouTubePlaylist|YouTubeAlbum)-([a-zA-Z0-9_-]{10,64})\]", RegexOptions.Compiled);
    private static readonly Regex EndOfFileNameVideoIdRegex = new(@"(?:[\s\-_])([a-zA-Z0-9_-]{11})(?:\.[a-zA-Z0-9]{2,4})?$", RegexOptions.Compiled);

    public static string? GetVideoId(IDictionary<string, string>? ids)
    {
        if (ids == null)
        {
            return null;
        }

        if (ids.TryGetValue(Video, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            return v;
        }

        if (ids.TryGetValue(Audio, out var a) && !string.IsNullOrWhiteSpace(a))
        {
            return a;
        }

        if (ids.TryGetValue(Movie, out var m) && !string.IsNullOrWhiteSpace(m))
        {
            return m;
        }

        if (ids.TryGetValue(MusicVideo, out var mv) && !string.IsNullOrWhiteSpace(mv))
        {
            return mv;
        }

        if (ids.TryGetValue("YouTube", out var yt) && !string.IsNullOrWhiteSpace(yt))
        {
            return yt;
        }

        return null;
    }

    public static void SetVideoId(IHasProviderIds item, string videoId)
    {
        item.SetProviderId(Video, videoId);
        item.SetProviderId("YouTube", videoId);
    }

    public static void SetAudioId(IHasProviderIds item, string videoId)
    {
        item.SetProviderId(Audio, videoId);
        item.SetProviderId(Video, videoId);
        item.SetProviderId("YouTube", videoId);
    }

    public static void SetMovieId(IHasProviderIds item, string videoId)
    {
        item.SetProviderId(Movie, videoId);
        item.SetProviderId(Video, videoId);
        item.SetProviderId("YouTube", videoId);
    }

    public static void SetMusicVideoId(IHasProviderIds item, string videoId)
    {
        item.SetProviderId(MusicVideo, videoId);
        item.SetProviderId(Video, videoId);
        item.SetProviderId("YouTube", videoId);
    }

    public static string? GetChannelId(IDictionary<string, string>? ids) =>
        ids != null && ids.TryGetValue(Channel, out var c) && !string.IsNullOrWhiteSpace(c) ? c : null;

    public static void SetChannelId(IHasProviderIds item, string channelId) =>
        item.SetProviderId(Channel, channelId);

    public static string? GetPlaylistId(IDictionary<string, string>? ids) =>
        ids != null && ids.TryGetValue(Playlist, out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;

    public static void SetPlaylistId(IHasProviderIds item, string playlistId) =>
        item.SetProviderId(Playlist, playlistId);

    public static string? ExtractVideoIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 1. Explicit YouTube plugin tag: [YouTubeVideo-XXXX] or [YouTubeAudio-XXXX]
        var m = VideoPathRegex.Match(path);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // 2. Standard yt-dlp brackets: [dQw4w9WgXcQ] (must not be channel UC...)
        var mBracket = StandardBracketVideoIdRegex.Match(path);
        if (mBracket.Success && !mBracket.Groups[1].Value.StartsWith("UC", StringComparison.Ordinal))
        {
            return mBracket.Groups[1].Value;
        }

        // 2b. Standard parentheses: (dQw4w9WgXcQ)
        var mParen = StandardParenVideoIdRegex.Match(path);
        if (mParen.Success && !mParen.Groups[1].Value.StartsWith("UC", StringComparison.Ordinal))
        {
            return mParen.Groups[1].Value;
        }

        // 3. Support companion .info.json next to video file
        try
        {
            var infoJson = Path.ChangeExtension(path, ".info.json");
            if (File.Exists(infoJson))
            {
                var content = File.ReadAllText(infoJson);
                var mId = Regex.Match(content, @"""id""\s*:\s*""([a-zA-Z0-9_-]{11})""");
                if (mId.Success)
                {
                    return mId.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore transient file read errors
        }

        // 4. Support companion .trailer.strm or generic .strm files with embedded YouTube URLs
        if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            try
            {
                var firstLine = File.ReadLines(path).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    return ExtractVideoIdFromUrlOrText(firstLine);
                }
            }
            catch
            {
                // Ignore transient file read errors
            }
        }

        // 5. Standard yt-dlp suffix: video-dQw4w9WgXcQ.mp4 or video_dQw4w9WgXcQ.mkv
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var mSuffix = EndOfFileNameVideoIdRegex.Match(fileName);
            if (mSuffix.Success && !mSuffix.Groups[1].Value.StartsWith("UC", StringComparison.Ordinal))
            {
                return mSuffix.Groups[1].Value;
            }
        }

        return null;
    }

    public static string? ExtractVideoIdFromUrlOrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = Regex.Match(text, @"(?:[?&]v=|youtu\.be\/|\/embed\/|\/stream\/|\[(?:YouTubeVideo|YouTubeAudio|YouTubeMusicVideo)-|\[)([a-zA-Z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ExtractChannelIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var m = ChannelPathRegex.Match(path);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        var mBracket = StandardBracketChannelRegex.Match(path);
        if (mBracket.Success)
        {
            return mBracket.Groups[1].Value;
        }

        var mRaw = RawChannelRegex.Match(path);
        return mRaw.Success ? mRaw.Groups[1].Value : null;
    }

    public static string? ExtractPlaylistIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var m = PlaylistPathRegex.Match(path);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsYouTubeItem(IHasProviderIds? item, string? path = null)
    {
        if (item?.ProviderIds != null)
        {
            if (item.ProviderIds.ContainsKey(Video) ||
                item.ProviderIds.ContainsKey(Audio) ||
                item.ProviderIds.ContainsKey(Movie) ||
                item.ProviderIds.ContainsKey(MusicVideo) ||
                item.ProviderIds.ContainsKey(Channel) ||
                item.ProviderIds.ContainsKey(Playlist) ||
                item.ProviderIds.ContainsKey(Album) ||
                item.ProviderIds.ContainsKey(Artist) ||
                item.ProviderIds.ContainsKey("YouTube") ||
                item.ProviderIds.ContainsKey("YouTubeMusic"))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            if (path.Contains("/data/youtube", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/data/youtube-music", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("YouTubeVideo-", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("YouTubeAudio-", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("YouTubeMusicVideo-", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("-trailer", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(".trailer.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (StandardBracketVideoIdRegex.IsMatch(path) || ChannelPathRegex.IsMatch(path) || StandardBracketChannelRegex.IsMatch(path))
            {
                return true;
            }

            try
            {
                var infoJson = Path.ChangeExtension(path, ".info.json");
                if (File.Exists(infoJson))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore transient I/O exceptions
            }
        }

        return false;
    }

    public static bool IsYouTubeDto(BaseItemDto? dto)
    {
        if (dto == null)
        {
            return false;
        }

        if (dto.ProviderIds != null)
        {
            if (dto.ProviderIds.ContainsKey(Video) ||
                dto.ProviderIds.ContainsKey(Audio) ||
                dto.ProviderIds.ContainsKey(Movie) ||
                dto.ProviderIds.ContainsKey(MusicVideo) ||
                dto.ProviderIds.ContainsKey(Channel) ||
                dto.ProviderIds.ContainsKey(Playlist) ||
                dto.ProviderIds.ContainsKey(Album) ||
                dto.ProviderIds.ContainsKey(Artist) ||
                dto.ProviderIds.ContainsKey("YouTube") ||
                dto.ProviderIds.ContainsKey("YouTubeMusic"))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(dto.Path))
        {
            if (dto.Path.Contains("/data/youtube", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("/data/youtube-music", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("YouTubeVideo-", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("YouTubeAudio-", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("YouTubeMusicVideo-", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("/api/youtubePlugin/stream/", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains("-trailer", StringComparison.OrdinalIgnoreCase) ||
                dto.Path.Contains(".trailer.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (StandardBracketVideoIdRegex.IsMatch(dto.Path) || ChannelPathRegex.IsMatch(dto.Path) || StandardBracketChannelRegex.IsMatch(dto.Path))
            {
                return true;
            }
        }

        return false;
    }
}

public class YouTubeChannelExternalId : IExternalId
{
    public string ProviderName => "YouTube Channel";
    public string Key => YouTubeIdKeys.Channel;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;
    public string? UrlFormatString => "https://www.youtube.com/channel/{0}";
    public bool Supports(IHasProviderIds item) => item is Series;
}

public class YouTubePlaylistExternalId : IExternalId
{
    public string ProviderName => "YouTube Playlist";
    public string Key => YouTubeIdKeys.Playlist;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Season;
    public string? UrlFormatString => "https://www.youtube.com/playlist?list={0}";
    public bool Supports(IHasProviderIds item) => item is Season;
}

public class YouTubeVideoExternalId : IExternalId
{
    public string ProviderName => "YouTube Video";
    public string Key => YouTubeIdKeys.Video;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Episode;
    public string? UrlFormatString => "https://www.youtube.com/watch?v={0}";
    public bool Supports(IHasProviderIds item) => item is Episode;
}

public class YouTubeMovieExternalId : IExternalId
{
    public string ProviderName => "YouTube Movie";
    public string Key => YouTubeIdKeys.Movie;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    public string? UrlFormatString => "https://www.youtube.com/watch?v={0}";
    public bool Supports(IHasProviderIds item) => item is Movie;
}

public class YouTubeMusicVideoExternalId : IExternalId
{
    public string ProviderName => "YouTube Music Video";
    public string Key => YouTubeIdKeys.MusicVideo;
    public ExternalIdMediaType? Type => null;
    public string? UrlFormatString => "https://www.youtube.com/watch?v={0}";
    public bool Supports(IHasProviderIds item) => item is MusicVideo;
}

public class YouTubeExternalUrlProvider : IExternalUrlProvider
{
    public string Name => "YouTube";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        var channelId = YouTubeIdKeys.GetChannelId(item.ProviderIds);
        if (!string.IsNullOrEmpty(channelId) && item is Series)
        {
            yield return $"https://www.youtube.com/channel/{channelId}";
        }

        var playlistId = YouTubeIdKeys.GetPlaylistId(item.ProviderIds);
        if (!string.IsNullOrEmpty(playlistId) && item is Season)
        {
            yield return $"https://www.youtube.com/playlist?list={playlistId}";
        }

        var videoId = YouTubeIdKeys.GetVideoId(item.ProviderIds);
        if (!string.IsNullOrEmpty(videoId) && item is Episode or Movie or Audio or MusicVideo)
        {
            if (item is Audio)
            {
                yield return $"https://music.youtube.com/watch?v={videoId}";
            }

            yield return $"https://www.youtube.com/watch?v={videoId}";
        }
    }
}

public class YouTubeAudioExternalId : IExternalId
{
    public string ProviderName => "YouTube Music";
    public string Key => YouTubeIdKeys.Audio;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Track;
    public string? UrlFormatString => "https://music.youtube.com/watch?v={0}";
    public bool Supports(IHasProviderIds item) => item is Audio;
}
