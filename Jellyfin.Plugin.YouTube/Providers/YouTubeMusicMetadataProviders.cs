using Jellyfin.Plugin.YouTube.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YouTube.Providers;

public class YouTubeAudioProvider : IRemoteMetadataProvider<Audio, SongInfo>, IHasOrder
{
    public string Name => "YouTube";
    public int Order => 3;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    public Task<MetadataResult<Audio>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Audio>();
        var videoId = YouTubeIdKeys.GetVideoId(info.ProviderIds)
            ?? YouTubeIdKeys.ExtractVideoIdFromPath(info.Path);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            return Task.FromResult(result);
        }

        var parentAlbum = CleanFolderName(Path.GetFileName(Path.GetDirectoryName(info.Path)));
        var parentArtist = CleanFolderName(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(info.Path))));
        var cleanTitle = CleanTrackTitle(info.Name, videoId);
        var artistName = cleanTitle.Contains(" - ", StringComparison.Ordinal)
            ? cleanTitle.Split(" - ", 2)[0].Trim()
            : (!string.IsNullOrWhiteSpace(parentArtist) ? parentArtist : "YouTube Music");
        var trackTitle = cleanTitle.Contains(" - ", StringComparison.Ordinal)
            ? cleanTitle.Split(" - ", 2)[1].Trim()
            : cleanTitle;

        var audio = new Audio
        {
            Name = trackTitle,
            Album = !string.IsNullOrWhiteSpace(parentAlbum) ? parentAlbum : "YouTube Music",
            Artists = new[] { artistName },
            AlbumArtists = new[] { !string.IsNullOrWhiteSpace(parentArtist) ? parentArtist : artistName },
            IndexNumber = info.IndexNumber ?? ExtractTrackNumber(info.Path) ?? 1,
            ParentIndexNumber = 1,
            PremiereDate = DateTime.UtcNow.Date,
            ProductionYear = DateTime.UtcNow.Year,
            CommunityRating = 8.8f,
            Overview = $"🎵 {artistName} — {trackTitle}\n🔗 https://music.youtube.com/watch?v={videoId}"
        };

        audio.AddGenre("YouTube Music");
        YouTubeIdKeys.SetAudioId(audio, videoId);
        audio.SetProviderId("YouTubeAudioKey", $"{parentAlbum}_{videoId}");

        result.Item = audio;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        new HttpClient().GetAsync(url, cancellationToken);

    internal static string CleanFolderName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var idx = raw.IndexOf("[YouTube", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? raw[..idx].Trim() : raw.Trim();
    }

    private static int? ExtractTrackNumber(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var file = Path.GetFileNameWithoutExtension(path);
        var m = System.Text.RegularExpressions.Regex.Match(file, @"^(?:S\d+E)?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    private static string CleanTrackTitle(string? rawName, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallbackId;
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(rawName, @"^(?:S\d+E\d+|\d+)\s*-\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var idx = cleaned.IndexOf("[YouTube", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? cleaned[..idx].Trim() : cleaned.Trim();
    }
}

public class YouTubeAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder
{
    public string Name => "YouTube";
    public int Order => 3;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    public Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicAlbum>();
        if (string.IsNullOrWhiteSpace(info.Path) || !info.Path.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(result);
        }

        var albumName = YouTubeAudioProvider.CleanFolderName(Path.GetFileName(info.Path));
        var artistName = YouTubeAudioProvider.CleanFolderName(Path.GetFileName(Path.GetDirectoryName(info.Path)));
        var album = new MusicAlbum
        {
            Name = !string.IsNullOrWhiteSpace(albumName) ? albumName : info.Name,
            AlbumArtists = !string.IsNullOrWhiteSpace(artistName) ? new[] { artistName } : new[] { "YouTube Music" },
            Artists = !string.IsNullOrWhiteSpace(artistName) ? new[] { artistName } : new[] { "YouTube Music" },
            ProductionYear = DateTime.UtcNow.Year,
            PremiereDate = DateTime.UtcNow.Date,
            Overview = $"YouTube Music Album / Playlist: {albumName}"
        };

        album.AddGenre("YouTube Music");
        album.SetProviderId(YouTubeIdKeys.Album, $"ytm_album_{albumName}");

        result.Item = album;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        new HttpClient().GetAsync(url, cancellationToken);
}

public class YouTubeArtistProvider : IRemoteMetadataProvider<MusicArtist, ArtistInfo>, IHasOrder
{
    public string Name => "YouTube";
    public int Order => 3;

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    public Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicArtist>();
        if (string.IsNullOrWhiteSpace(info.Path) || !info.Path.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(result);
        }

        var artistName = YouTubeAudioProvider.CleanFolderName(Path.GetFileName(info.Path));
        var artist = new MusicArtist
        {
            Name = !string.IsNullOrWhiteSpace(artistName) ? artistName : info.Name,
            Overview = $"YouTube Music Artist / Feed: {artistName}"
        };

        artist.AddGenre("YouTube Music");
        artist.SetProviderId(YouTubeIdKeys.Artist, $"ytm_artist_{artistName}");

        result.Item = artist;
        result.HasMetadata = true;
        return Task.FromResult(result);
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        new HttpClient().GetAsync(url, cancellationToken);
}
