using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YouTube.Providers;

public static class YouTubeIdKeys
{
    public const string Video = "YouTubeVideo";
    public const string Channel = "YouTubeChannel";
    public const string Playlist = "YouTubePlaylist";
    public const string Movie = "YouTubeMovie";
    public const string Audio = "YouTubeAudio";
    public const string Album = "YouTubeAlbum";
    public const string Artist = "YouTubeArtist";

    private static readonly Regex VideoPathRegex = new(@"\[(?:YouTubeVideo|YouTubeAudio)-([a-zA-Z0-9_-]{11})\]", RegexOptions.Compiled);
    private static readonly Regex ChannelPathRegex = new(@"\[(?:YouTubeChannel|YouTubeArtist)-(UC[a-zA-Z0-9_-]{20,28})\]", RegexOptions.Compiled);
    private static readonly Regex PlaylistPathRegex = new(@"\[(?:YouTubePlaylist|YouTubeAlbum)-([a-zA-Z0-9_-]{10,64})\]", RegexOptions.Compiled);

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

        var m = VideoPathRegex.Match(path);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // Support companion .trailer.strm or generic .strm files with embedded YouTube URLs
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

        return null;
    }

    public static string? ExtractVideoIdFromUrlOrText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = Regex.Match(text, @"(?:[?&]v=|youtu\.be\/|\/embed\/|\/stream\/|\[(?:YouTubeVideo|YouTubeAudio)-)([a-zA-Z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ExtractChannelIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var m = ChannelPathRegex.Match(path);
        return m.Success ? m.Groups[1].Value : null;
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
        if (!string.IsNullOrEmpty(videoId) && item is Episode or Movie or Audio)
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
