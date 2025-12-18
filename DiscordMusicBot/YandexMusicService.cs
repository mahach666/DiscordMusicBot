using Yandex.Music.Api;
using Yandex.Music.Api.Common;
using Yandex.Music.Api.Models.Search.Track;
using Yandex.Music.Api.Models.Track;

namespace DiscordMusicBot;

public sealed record YandexMusicTrack(
    string Id,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumId);

public sealed class YandexMusicService
{
    private readonly string? _token;
    private readonly AuthStorage _storage;
    private readonly YandexMusicApi _api;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private volatile bool _authAttempted;

    public YandexMusicService(string? token = null)
    {
        _token = token;
        _storage = new AuthStorage();
        _api = new YandexMusicApi();
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_token);

    private async Task<bool> EnsureAuthorizedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return false;
        }

        if (_storage.IsAuthorized)
        {
            return true;
        }

        if (_authAttempted)
        {
            return false;
        }

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            if (_storage.IsAuthorized)
            {
                return true;
            }

            if (_authAttempted)
            {
                return false;
            }

            _authAttempted = true;
            await _api.User.AuthorizeAsync(_storage, _token!);
            return _storage.IsAuthorized;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music authorize error: {ex.Message}");
            return false;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<List<YandexMusicTrack>> SearchTracksAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return [];
        }

        try
        {
            var pageSize = Math.Clamp(limit, 1, 50);
            var response = await _api.Search.TrackAsync(_storage, query, pageNumber: 0, pageSize: pageSize);
            var results = response?.Result?.Tracks?.Results ?? [];

            return results
                .OfType<YSearchTrackModel>()
                .Take(limit)
                .Select(t => new YandexMusicTrack(
                    Id: t.Id,
                    Title: t.Title,
                    Artists: t.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [],
                    AlbumId: t.Albums?.FirstOrDefault()?.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music search error: {ex.Message}");
            return [];
        }
    }

    public async Task<YandexMusicTrack?> GetTrackByIdAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            var response = await _api.Track.GetAsync(_storage, trackId);
            var track = response?.Result?.FirstOrDefault();
            if (track == null || string.IsNullOrWhiteSpace(track.Id))
            {
                return null;
            }

            return new YandexMusicTrack(
                Id: track.Id,
                Title: track.Title,
                Artists: track.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [],
                AlbumId: track.Albums?.FirstOrDefault()?.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music get track error: {ex.Message}");
            return null;
        }
    }

    public async Task<(string? Title, List<YandexMusicTrack> Tracks)> GetPlaylistAsync(
        string user,
        string kind,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return (null, []);
        }

        try
        {
            var response = await _api.Playlist.GetAsync(_storage, user, kind);
            var playlist = response?.Result;
            if (playlist == null)
            {
                return (null, []);
            }

            var title = playlist.Title;
            var containers = playlist.Tracks ?? new List<YTrackContainer>();

            var tracks = containers
                .Where(c => c?.Track != null && !string.IsNullOrWhiteSpace(c.Track.Id))
                .Select(c => new YandexMusicTrack(
                    Id: c.Track.Id,
                    Title: c.Track.Title,
                    Artists: c.Track.Artists?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [],
                    AlbumId: !string.IsNullOrWhiteSpace(c.AlbumId) ? c.AlbumId : c.Track.Albums?.FirstOrDefault()?.Id))
                .ToList();

            return (title, tracks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music get playlist error: {ex.Message}");
            return (null, []);
        }
    }

    public async Task<string?> GetDownloadUrlByIdAsync(string trackId, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            var response = await _api.Track.GetAsync(_storage, trackId);
            var track = response?.Result?.FirstOrDefault();
            return track == null ? null : await GetDownloadUrlAsync(track, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music get track error: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> GetDownloadUrlAsync(YTrack track, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            var trackKey = track.GetKey().ToString();
            if (string.IsNullOrWhiteSpace(trackKey) || !trackKey.Contains(':'))
            {
                return null;
            }

            return await _api.Track.GetFileLinkAsync(_storage, trackKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music get file link error: {ex.Message}");
            return null;
        }
    }

    public async Task<string?> GetDownloadUrlAsync(YandexMusicTrack track, CancellationToken cancellationToken = default)
    {
        if (!await EnsureAuthorizedAsync(cancellationToken))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(track.AlbumId))
        {
            return await GetDownloadUrlByIdAsync(track.Id, cancellationToken);
        }

        try
        {
            var trackKey = $"{track.Id}:{track.AlbumId}";
            return await _api.Track.GetFileLinkAsync(_storage, trackKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Yandex Music get file link error: {ex.Message}");
            return null;
        }
    }

    public string GetTrackUrl(YandexMusicTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.AlbumId))
        {
            return $"https://music.yandex.ru/album/{track.AlbumId}/track/{track.Id}";
        }

        return $"https://music.yandex.ru/track/{track.Id}";
    }

    public string GetTrackTitle(YandexMusicTrack track)
    {
        var artists = track.Artists == null ? string.Empty : string.Join(", ", track.Artists);
        if (string.IsNullOrWhiteSpace(artists) || string.IsNullOrWhiteSpace(track.Title))
        {
            return track.Title ?? track.Id;
        }

        return $"{artists} - {track.Title}";
    }
}
