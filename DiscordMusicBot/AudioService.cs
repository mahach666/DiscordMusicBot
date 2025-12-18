using Discord;
using Discord.WebSocket;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Victoria;
using Victoria.Enums;
using Victoria.Rest.Search;
using Victoria.WebSocket.EventArgs;

namespace DiscordMusicBot;

public class AudioService
{
    private const string LavalinkHost = "127.0.0.1";
    private const int LavalinkPort = 2333;
    private const string LavalinkPassword = "youshallnotpass";
    private const int LavalinkApiVersion = 4;
    private static readonly TimeSpan IdleDisconnectDelay = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions LavalinkPatchJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly LavaNode _lavaNode;
    private readonly StreamingPreferencesService _streamingPreferences;
    private readonly LikesService _likesService;
    private readonly YandexMusicService _yandexMusicService;
    private readonly DiscordSocketClient _discordClient;
    private readonly HttpClient _lavalinkHttpClient;
    private readonly ConcurrentDictionary<ulong, MusicQueue> _queues = new();
    private readonly ConcurrentDictionary<ulong, LimitedStack<TrackState>> _history = new();
    private readonly ConcurrentDictionary<ulong, TimeSpan> _pausedPositions = new();
    private readonly ConcurrentDictionary<ulong, ulong> _voiceChannelIds = new();
    private readonly ConcurrentDictionary<ulong, IVoiceChannel> _voiceChannels = new();
    private readonly ConcurrentDictionary<ulong, TrackState> _currentTracks = new();
    private readonly ConcurrentDictionary<ulong, int> _volumes = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _idleDisconnectTokens = new();
    private readonly ConcurrentDictionary<ulong, LikedShuffleState> _likedShuffle = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _likedShuffleLocks = new();
    private PlayerUiService? _playerUiService;

    public event Func<ulong, Task>? PlayerStateChanged;

    public AudioService(
        LavaNode lavaNode,
        StreamingPreferencesService streamingPreferences,
        LikesService likesService,
        YandexMusicService yandexMusicService,
        DiscordSocketClient discordClient)
    {
        _lavaNode = lavaNode;
        _streamingPreferences = streamingPreferences;
        _likesService = likesService;
        _yandexMusicService = yandexMusicService;
        _discordClient = discordClient;
        _lavalinkHttpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{LavalinkHost}:{LavalinkPort}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        _lavalinkHttpClient.DefaultRequestHeaders.Add("Authorization", LavalinkPassword);

        _lavaNode.OnTrackStart += OnTrackStartAsync;
        _lavaNode.OnTrackEnd += OnTrackEndAsync;
    }

    public int GetQueueCount(ulong guildId) => _queues.TryGetValue(guildId, out var queue) ? queue.Count : 0;
    public int GetHistoryCount(ulong guildId) => _history.TryGetValue(guildId, out var history) ? history.Count : 0;
    public int GetVolume(ulong guildId) => _volumes.TryGetValue(guildId, out var volume) ? volume : 100;
    public bool TryGetVoiceChannelId(ulong guildId, out ulong voiceChannelId) => _voiceChannelIds.TryGetValue(guildId, out voiceChannelId);

    public bool TryGetCurrentTrack(ulong guildId, out LavaTrack track)
    {
        if (_currentTracks.TryGetValue(guildId, out var state))
        {
            track = state.Track;
            return true;
        }

        track = default!;
        return false;
    }

    public bool TryGetCurrentTrackState(ulong guildId, out TrackState state) => _currentTracks.TryGetValue(guildId, out state);

    public void SetPlayerUiService(PlayerUiService playerUiService)
    {
        _playerUiService = playerUiService;
    }

    private ulong? GetBotVoiceChannelId(ulong guildId)
    {
        try
        {
            return _discordClient.GetGuild(guildId)?.CurrentUser?.VoiceChannel?.Id;
        }
        catch
        {
            return null;
        }
    }

    public async Task JoinAsync(IVoiceChannel voiceChannel, ITextChannel textChannel)
    {
        CancelIdleDisconnect(voiceChannel.GuildId);

        var existingPlayer = await _lavaNode.TryGetPlayerAsync(voiceChannel.GuildId);
        if (existingPlayer != null)
        {
            var botVoiceChannelId = GetBotVoiceChannelId(voiceChannel.GuildId);
            if (botVoiceChannelId == voiceChannel.Id)
            {
                // Иногда player существует, но локальные стейты потерялись (или бот переподключили извне).
                _queues.TryAdd(voiceChannel.GuildId, new MusicQueue());
                _history.TryAdd(voiceChannel.GuildId, new LimitedStack<TrackState>(25));
                _voiceChannelIds[voiceChannel.GuildId] = voiceChannel.Id;
                _voiceChannels[voiceChannel.GuildId] = voiceChannel;
                _volumes.TryAdd(voiceChannel.GuildId, 100);
                await textChannel.SendMessageAsync("Я уже подключен к голосовому каналу.");
                return;
            }

            try
            {
                await _lavaNode.DestroyPlayerAsync(voiceChannel.GuildId);
            }
            catch
            {
                // ignore destroy failures
            }
        }

        if (!_lavaNode.IsConnected)
        {
            await textChannel.SendMessageAsync("Lavalink не подключен. Убедитесь, что Lavalink запущен и бот подключился к нему.");
            return;
        }

        try
        {
            await _lavaNode.JoinAsync(voiceChannel);

            _queues.TryAdd(voiceChannel.GuildId, new MusicQueue());
            _history.TryAdd(voiceChannel.GuildId, new LimitedStack<TrackState>(25));
            _voiceChannelIds[voiceChannel.GuildId] = voiceChannel.Id;
            _voiceChannels[voiceChannel.GuildId] = voiceChannel;
            _volumes.TryAdd(voiceChannel.GuildId, 100);
            await NotifyPlayerStateChangedAsync(voiceChannel.GuildId);
            UpdateIdleDisconnectState(voiceChannel.GuildId);

            await textChannel.SendMessageAsync($"Подключился к {voiceChannel.Name}");
        }
        catch (Exception ex)
        {
            await textChannel.SendMessageAsync($"Не удалось подключиться к голосовому каналу: {ex.Message}");
        }
    }

    public async Task LeaveAsync(IGuild guild, ITextChannel textChannel)
    {
        CancelIdleDisconnect(guild.Id);
        DisableLikedShuffle(guild.Id);

        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        if (player == null)
        {
            await textChannel.SendMessageAsync("Я не подключен к голосовому каналу.");
            return;
        }

        if (_voiceChannelIds.TryGetValue(guild.Id, out var voiceChannelId))
        {
            var voiceChannel = await guild.GetVoiceChannelAsync(voiceChannelId);
            if (voiceChannel != null)
            {
                try
                {
                    await _lavaNode.LeaveAsync(voiceChannel);
                }
                catch
                {
                    // ignore leave failures
                }
            }
        }

        // Важно: обязательно удаляем player в Lavalink, иначе следующий !p/!join может увидеть его "существующим" и не подключиться.
        try
        {
            await _lavaNode.DestroyPlayerAsync(guild.Id);
        }
        catch
        {
            // ignore destroy failures
        }

        _queues.TryRemove(guild.Id, out _);
        _history.TryRemove(guild.Id, out _);
        _voiceChannelIds.TryRemove(guild.Id, out _);
        _voiceChannels.TryRemove(guild.Id, out _);
        _currentTracks.TryRemove(guild.Id, out _);
        _pausedPositions.TryRemove(guild.Id, out _);
        await NotifyPlayerStateChangedAsync(guild.Id);

        await textChannel.SendMessageAsync("Отключился.");
    }

    public async Task PlayAsync(string query, IGuild guild, IVoiceChannel voiceChannel, ITextChannel textChannel)
    {
        DisableLikedShuffle(guild.Id);

        if (string.IsNullOrWhiteSpace(query))
        {
            await textChannel.SendMessageAsync("Укажите запрос или URL.");
            return;
        }

        query = query.Trim();

        var attempts = 0;
        const int maxAttempts = 5;

        while (attempts < maxAttempts && !_lavaNode.IsConnected)
        {
            await Task.Delay(1000);
            attempts++;
        }

        if (!_lavaNode.IsConnected)
        {
            await textChannel.SendMessageAsync("Lavalink не подключен. Подождите или перезапустите Lavalink.");
            return;
        }

        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        var botVoiceChannelId = GetBotVoiceChannelId(guild.Id);
        var needsJoin =
            player == null
            || botVoiceChannelId == null
            || !_voiceChannelIds.TryGetValue(guild.Id, out var currentVoiceChannelId)
            || currentVoiceChannelId != voiceChannel.Id
            || botVoiceChannelId != voiceChannel.Id;

        if (needsJoin)
        {
            await JoinAsync(voiceChannel, textChannel);
            player = await _lavaNode.TryGetPlayerAsync(guild.Id);
            if (player == null)
            {
                return;
            }
        }

        if (player == null)
        {
            return;
        }

        CancelIdleDisconnect(guild.Id);

        var isUrl = Uri.TryCreate(query, UriKind.Absolute, out var parsedUri);
        var isExplicitIdentifier = IsLavalinkIdentifier(query);
        var isPlainTextQuery = !isUrl && !isExplicitIdentifier;

        if (isUrl && parsedUri != null)
        {
            query = TryNormalizeYouTubeUrl(parsedUri, query);
        }

        var identifier = query;
        var plainTextPrimary = StreamingSource.Auto;
        if (isPlainTextQuery)
        {
            plainTextPrimary = await _streamingPreferences.GetPreferredSourceAsync(guild.Id);
            identifier = BuildSearchIdentifier(query, plainTextPrimary);
        }

        string? externalTitle = null;
        string? externalAuthor = null;
        string? externalSourceName = null;
        string? externalUrl = null;

        // Handle Yandex Music search
        if (identifier.StartsWith("yandexmusicsearch:", StringComparison.OrdinalIgnoreCase))
        {
            if (!_yandexMusicService.IsEnabled)
            {
                await textChannel.SendMessageAsync("Yandex Music не настроен. Добавьте YANDEX_MUSIC_TOKEN в переменные окружения.");
                return;
            }

            var yandexQuery = identifier["yandexmusicsearch:".Length..];
            Console.WriteLine($"Yandex Music search query: {yandexQuery}");

            var yandexTracks = await _yandexMusicService.SearchTracksAsync(yandexQuery, limit: 1);
            Console.WriteLine($"Yandex Music found tracks: {yandexTracks.Count}");

            if (yandexTracks.Count == 0)
            {
                await textChannel.SendMessageAsync("Ничего не найдено на Yandex Music.");
                return;
            }

            var yandexTrack = yandexTracks[0];
            var trackUrl = _yandexMusicService.GetTrackUrl(yandexTrack);
            Console.WriteLine($"Yandex Music track URL: {trackUrl}");

            externalTitle = yandexTrack.Title;
            externalAuthor = yandexTrack.Artists.Count > 0 ? string.Join(", ", yandexTrack.Artists) : "Yandex Music";
            externalSourceName = "Yandex Music";
            externalUrl = trackUrl;

            // Пытаемся получить download URL
            var downloadUrl = await _yandexMusicService.GetDownloadUrlAsync(yandexTrack);
            Console.WriteLine($"Yandex Music download URL: {downloadUrl}");

            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                // Используем HTTP source с download URL
                identifier = downloadUrl;
            }
            else
            {
                // Fallback to direct Yandex Music link
                identifier = trackUrl;
            }
        }

        // Handle direct Yandex Music URLs
        if (parsedUri != null && parsedUri.Host.Contains("music.yandex", StringComparison.OrdinalIgnoreCase))
        {
            if (!_yandexMusicService.IsEnabled)
            {
                await textChannel.SendMessageAsync("Yandex Music не настроен. Добавьте YANDEX_MUSIC_TOKEN в переменные окружения.");
                return;
            }

            // Пытаемся извлечь track ID из URL
            var path = parsedUri.AbsolutePath;

            // Yandex Music playlists: https://music.yandex.ru/users/<user>/playlists/<id>
            if (path.Contains("/playlists/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var usersIndex = Array.FindIndex(segments, s => s.Equals("users", StringComparison.OrdinalIgnoreCase));
                var playlistsIndex = Array.FindIndex(segments, s => s.Equals("playlists", StringComparison.OrdinalIgnoreCase));

                if (usersIndex >= 0
                    && playlistsIndex >= 0
                    && usersIndex + 1 < segments.Length
                    && playlistsIndex + 1 < segments.Length)
                {
                    var user = segments[usersIndex + 1];
                    var kind = segments[playlistsIndex + 1];

                    await EnqueueYandexPlaylistAsync(guild, voiceChannel, textChannel, player, user, kind);
                    return;
                }
            }
            if (path.Contains("/track/"))
            {
                var trackIdPart = path.Split("/track/").Last().Split('?').First();
                Console.WriteLine($"Extracted Yandex Music track ID: {trackIdPart}");

                try
                {
                    var ymTrack = await _yandexMusicService.GetTrackByIdAsync(trackIdPart);
                    if (ymTrack != null)
                    {
                        externalTitle = ymTrack.Title;
                        externalAuthor = ymTrack.Artists.Count > 0 ? string.Join(", ", ymTrack.Artists) : "Yandex Music";
                        externalSourceName = "Yandex Music";
                        externalUrl = _yandexMusicService.GetTrackUrl(ymTrack);
                    }
                }
                catch
                {
                    // ignore metadata lookup failures
                }

                // Получаем информацию о треке и download URL
                var downloadUrl = await _yandexMusicService.GetDownloadUrlByIdAsync(trackIdPart);
                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    identifier = downloadUrl;
                }
                else
                {
                    await textChannel.SendMessageAsync("Не удалось получить доступ к треку с Yandex Music.");
                    return;
                }
            }
        }

        SearchResponse searchResponse;
        try
        {
            searchResponse = await _lavaNode.LoadTrackAsync(identifier);
        }
        catch (Exception ex)
        {
            await textChannel.SendMessageAsync($"Ошибка загрузки трека: {ex.Message}");
            return;
        }

        if (searchResponse == null)
        {
            await textChannel.SendMessageAsync("Lavalink вернул пустой ответ при загрузке трека.");
            return;
        }

        var triedSoundCloudFallback = false;
        var usedSoundCloudFallback = false;
        if (searchResponse.Type == SearchType.Error && isPlainTextQuery && plainTextPrimary == StreamingSource.Auto)
        {
            triedSoundCloudFallback = true;
            var fallbackIdentifier = $"scsearch:{query}";
            try
            {
                var fallbackResponse = await _lavaNode.LoadTrackAsync(fallbackIdentifier);
                if (fallbackResponse != null
                    && fallbackResponse.Type is not (SearchType.Error or SearchType.Empty)
                    && fallbackResponse.Tracks is { Count: > 0 })
                {
                    usedSoundCloudFallback = true;
                    identifier = fallbackIdentifier;
                    searchResponse = fallbackResponse;
                }
            }
            catch
            {
                // ignore fallback failures
            }
        }

        if (searchResponse.Type == SearchType.Error)
        {
            var exception = searchResponse.Exception;
            var message = exception.Message?.Trim();
            var cause = exception.Cause?.Trim();

            var isYouTubeQuery =
                identifier.StartsWith("ytsearch:", StringComparison.OrdinalIgnoreCase)
                || identifier.StartsWith("ytmsearch:", StringComparison.OrdinalIgnoreCase)
                || (parsedUri != null && (parsedUri.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase) || parsedUri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)));

            var details = message;
            if (string.IsNullOrWhiteSpace(details)
                || string.Equals(details, "Something went wrong while looking up the track.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(details, cause, StringComparison.OrdinalIgnoreCase))
            {
                details = cause;
            }

            if (string.IsNullOrWhiteSpace(details))
            {
                details = "Lavalink вернул ошибку при загрузке трека.";
            }

            if (triedSoundCloudFallback && !usedSoundCloudFallback)
            {
                details += "\n\nТакже попробовал SoundCloud (`scsearch:`), но ничего не нашёл.";
            }

            if (isYouTubeQuery)
            {
                details += "\n\nЕсли YouTube временно не работает, переключи источник по умолчанию: `!source soundcloud` (или верни авто: `!source auto`).";
            }

            if (details.Length > 1800)
            {
                details = $"{details[..1800]}...";
            }

            await textChannel.SendMessageAsync($"Ошибка Lavalink: {details}");
            return;
        }

        var tracks = searchResponse.Tracks;
        if (searchResponse.Type == SearchType.Empty || tracks == null || tracks.Count == 0)
        {
            await textChannel.SendMessageAsync("Ничего не найдено.");
            return;
        }

        if (searchResponse.Type == SearchType.Playlist)
        {
            var playlistItems = tracks
                .Select(t => new TrackState(t, TrackDisplay.From(t)))
                .ToList();

            var playlistQueue = _queues.GetOrAdd(guild.Id, _ => new MusicQueue());

            if (_currentTracks.ContainsKey(guild.Id))
            {
                foreach (var item in playlistItems)
                {
                    playlistQueue.Enqueue(item);
                }

                await NotifyPlayerStateChangedAsync(guild.Id);
                await textChannel.SendMessageAsync($"Добавлено **{playlistItems.Count}** трек(ов) из плейлиста в очередь.");
                return;
            }

            for (var i = 1; i < playlistItems.Count; i++)
            {
                playlistQueue.Enqueue(playlistItems[i]);
            }

            await PlayInternalAsync(guild.Id, player, playlistItems[0]);
            await NotifyPlayerStateChangedAsync(guild.Id);

            var playlistSourceSuffix = string.IsNullOrWhiteSpace(playlistItems[0].Display.SourceName) ? string.Empty : $" ({playlistItems[0].Display.SourceName})";
            await textChannel.SendMessageAsync($"Сейчас играет{playlistSourceSuffix}: **{playlistItems[0].Display.Title}**\nДобавлено в очередь: **{playlistItems.Count - 1}**");
            return;
        }

        var lavaTrack = tracks.First();
        var display = TrackDisplay.From(lavaTrack);

        if (!string.IsNullOrWhiteSpace(externalTitle))
        {
            display = display with { Title = externalTitle };
        }

        if (!string.IsNullOrWhiteSpace(externalAuthor))
        {
            display = display with { Author = externalAuthor };
        }

        if (!string.IsNullOrWhiteSpace(externalSourceName))
        {
            display = display with { SourceName = externalSourceName };
        }

        if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            display = display with { Url = externalUrl };
        }

        var track = new TrackState(lavaTrack, display);
        var queue = _queues.GetOrAdd(guild.Id, _ => new MusicQueue());
        var sourceSuffix = string.IsNullOrWhiteSpace(display.SourceName) ? string.Empty : $" ({display.SourceName})";

        if (_currentTracks.ContainsKey(guild.Id))
        {
            queue.Enqueue(track);
            await NotifyPlayerStateChangedAsync(guild.Id);
            await textChannel.SendMessageAsync($"Добавлено в очередь{sourceSuffix}: **{track.Display.Title}**");
            return;
        }

        await PlayInternalAsync(guild.Id, player, track);
        await textChannel.SendMessageAsync($"Сейчас играет{sourceSuffix}: **{track.Display.Title}**");
    }

    public async Task SkipAsync(IGuild guild, ITextChannel textChannel)
    {
        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        if (player == null)
        {
            await textChannel.SendMessageAsync("Я не подключен к голосовому каналу.");
            return;
        }

        if (!_queues.TryGetValue(guild.Id, out var queue) || queue.Count == 0)
        {
            await textChannel.SendMessageAsync("Очередь пуста.");
            return;
        }

        if (_currentTracks.TryGetValue(guild.Id, out var currentTrack))
        {
            _history.GetOrAdd(guild.Id, _ => new LimitedStack<TrackState>(25)).Push(currentTrack);
        }

        var nextTrack = queue.Dequeue();
        await PlayInternalAsync(guild.Id, player, nextTrack);
        await textChannel.SendMessageAsync($"Пропуск. Сейчас играет: **{nextTrack.Display.Title}**");
    }

    public async Task PauseAsync(IGuild guild, ITextChannel textChannel)
    {
        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        if (player == null)
        {
            await textChannel.SendMessageAsync("Я не подключен к голосовому каналу.");
            return;
        }

        if (!_currentTracks.ContainsKey(guild.Id))
        {
            await textChannel.SendMessageAsync("Сейчас ничего не играет.");
            return;
        }

        if (player.IsPaused)
        {
            // Возобновление воспроизведения
            if (_currentTracks.TryGetValue(guild.Id, out var currentTrack))
            {
                var usedFallback = false;
                try
                {
                    await SetPausedAsync(guild.Id, paused: false);
                }
                catch
                {
                    usedFallback = true;
                    await player.ResumeAsync(_lavaNode, currentTrack.Track);
                }

                // Если есть сохраненная позиция и трек поддерживает seek, переходим к ней
                if (usedFallback && _pausedPositions.TryGetValue(guild.Id, out var savedPosition) &&
                    currentTrack.Track.IsSeekable &&
                    savedPosition > TimeSpan.Zero &&
                    savedPosition < currentTrack.Track.Duration)
                {
                    await Task.Delay(100); // Небольшая задержка для стабильности
                    await player.SeekAsync(_lavaNode, savedPosition);
                }
            }

            _pausedPositions.TryRemove(guild.Id, out _);
            await NotifyPlayerStateChangedAsync(guild.Id);
            await textChannel.SendMessageAsync("Продолжаю воспроизведение.");
            return;
        }

        // Пауза - сохраняем текущую позицию
        _pausedPositions[guild.Id] = player.State.Position;
        await player.PauseAsync(_lavaNode);
        await NotifyPlayerStateChangedAsync(guild.Id);
        UpdateIdleDisconnectState(guild.Id);
        await textChannel.SendMessageAsync("Пауза.");
    }

    public async Task StopAsync(IGuild guild, ITextChannel textChannel)
    {
        CancelIdleDisconnect(guild.Id);
        DisableLikedShuffle(guild.Id);

        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        if (player == null)
        {
            await textChannel.SendMessageAsync("Я не подключен к голосовому каналу.");
            return;
        }

        // Получаем текущий трек для остановки
        _currentTracks.TryGetValue(guild.Id, out _);

        // Агрессивная очистка всех состояний ПЕРЕД остановкой плеера
        _currentTracks.TryRemove(guild.Id, out _);
        _pausedPositions.TryRemove(guild.Id, out _);
        if (_queues.TryGetValue(guild.Id, out var queue))
        {
            queue.Clear();
        }

        if (_history.TryGetValue(guild.Id, out var history))
        {
            history.Clear();
        }

        // Полностью останавливаем плеер
        try
        {
            await StopTrackAsync(guild.Id);
        }
        catch
        {
            await player.PauseAsync(_lavaNode);
        }

        // Удаляем сообщение плеера из чата
        if (_playerUiService != null)
        {
            await _playerUiService.RemovePlayerMessageAsync(guild.Id);
        }

        await NotifyPlayerStateChangedAsync(guild.Id);
        UpdateIdleDisconnectState(guild.Id);
        await textChannel.SendMessageAsync("Остановлено, очередь очищена.");
    }

    public void DisableLikedShuffle(ulong guildId)
    {
        _likedShuffle.TryRemove(guildId, out _);
    }

    public bool TryGetLikedShuffleUserId(ulong guildId, out ulong userId)
    {
        if (_likedShuffle.TryGetValue(guildId, out var state))
        {
            userId = state.UserId;
            return true;
        }

        userId = default;
        return false;
    }

    public async Task<PlayerActionResult> TryLikeCurrentTrackAsync(ulong guildId, ulong userId)
    {
        if (!_likesService.IsEnabled)
        {
            return new PlayerActionResult(false, "База данных не настроена. Лайки недоступны без Postgres.");
        }

        if (!_currentTracks.TryGetValue(guildId, out var track))
        {
            return new PlayerActionResult(false, "Сейчас ничего не играет.");
        }

        var result = await _likesService.LikeAsync(guildId, userId, track);
        return new PlayerActionResult(result.Success, result.Message);
    }

    public async Task<PlayerActionResult> StartLikedShuffleAsync(IGuild guild, IVoiceChannel voiceChannel, ITextChannel textChannel, ulong userId)
    {
        if (!_likesService.IsEnabled)
        {
            return new PlayerActionResult(false, "База данных не настроена. Лайки недоступны без Postgres.");
        }

        var botVoiceChannelId = GetBotVoiceChannelId(guild.Id);
        if (botVoiceChannelId != null && botVoiceChannelId != voiceChannel.Id)
        {
            return new PlayerActionResult(false, "Зайди в тот же голосовой канал, что и бот, чтобы включить лайки.");
        }

        CancelIdleDisconnect(guild.Id);

        var attempts = 0;
        const int maxAttempts = 5;
        while (attempts < maxAttempts && !_lavaNode.IsConnected)
        {
            await Task.Delay(1000);
            attempts++;
        }

        if (!_lavaNode.IsConnected)
        {
            return new PlayerActionResult(false, "Lavalink не подключен. Подождите или перезапустите Lavalink.");
        }

        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        var needsJoin =
            player == null
            || botVoiceChannelId == null
            || botVoiceChannelId != voiceChannel.Id;

        if (needsJoin)
        {
            await JoinAsync(voiceChannel, textChannel);
            player = await _lavaNode.TryGetPlayerAsync(guild.Id);
            if (player == null)
            {
                return new PlayerActionResult(false, "Не удалось подключиться к голосовому каналу.");
            }
        }

        // Новый режим — очищаем очередь, чтобы не смешивать с лайками
        _pausedPositions.TryRemove(guild.Id, out _);
        _currentTracks.TryRemove(guild.Id, out _);
        _queues.GetOrAdd(guild.Id, _ => new MusicQueue()).Clear();

        try
        {
            await StopTrackAsync(guild.Id);
        }
        catch
        {
            // ignore
        }

        var state = new LikedShuffleState(userId);
        _likedShuffle[guild.Id] = state;

        var like = await _likesService.GetRandomLikeAsync(guild.Id, userId);
        if (like == null)
        {
            DisableLikedShuffle(guild.Id);
            return new PlayerActionResult(false, "У вас нет лайков. Поставьте лайк текущему треку: `!like`");
        }

        var loaded = await LoadSingleTrackAsync(like.TrackUrl);
        if (loaded.Track == null)
        {
            DisableLikedShuffle(guild.Id);
            return new PlayerActionResult(false, $"Не удалось загрузить трек из лайков: {loaded.ErrorMessage}");
        }

        state.AddRecent(like.TrackUrl);
        var trackState = new TrackState(
            loaded.Track,
            new TrackDisplay(like.Title, like.Author, like.SourceName, like.TrackUrl));
        await PlayInternalAsync(guild.Id, player, trackState);
        await NotifyPlayerStateChangedAsync(guild.Id);
        return new PlayerActionResult(true, $"Режим лайков включен. Сейчас играет: **{trackState.Display.Title}**");
    }

    public async Task<PlayerActionResult> PlayLikedAsync(IGuild guild, IVoiceChannel voiceChannel, ITextChannel textChannel, ulong userId, long likeId)
    {
        if (!_likesService.IsEnabled)
        {
            return new PlayerActionResult(false, "База данных не настроена. Лайки недоступны без Postgres.");
        }

        var botVoiceChannelId = GetBotVoiceChannelId(guild.Id);
        if (botVoiceChannelId != null && botVoiceChannelId != voiceChannel.Id)
        {
            return new PlayerActionResult(false, "Зайди в тот же голосовой канал, что и бот, чтобы управлять лайками.");
        }

        var like = await _likesService.GetLikeAsync(guild.Id, userId, likeId);
        if (like == null)
        {
            return new PlayerActionResult(false, "Трек не найден в ваших лайках.");
        }

        CancelIdleDisconnect(guild.Id);

        var attempts = 0;
        const int maxAttempts = 5;
        while (attempts < maxAttempts && !_lavaNode.IsConnected)
        {
            await Task.Delay(1000);
            attempts++;
        }

        if (!_lavaNode.IsConnected)
        {
            return new PlayerActionResult(false, "Lavalink не подключен. Подождите или перезапустите Lavalink.");
        }

        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        var needsJoin =
            player == null
            || botVoiceChannelId == null
            || botVoiceChannelId != voiceChannel.Id;

        if (needsJoin)
        {
            await JoinAsync(voiceChannel, textChannel);
            player = await _lavaNode.TryGetPlayerAsync(guild.Id);
            if (player == null)
            {
                return new PlayerActionResult(false, "Не удалось подключиться к голосовому каналу.");
            }
        }

        _pausedPositions.TryRemove(guild.Id, out _);
        _currentTracks.TryRemove(guild.Id, out _);
        _queues.GetOrAdd(guild.Id, _ => new MusicQueue()).Clear();

        try
        {
            await StopTrackAsync(guild.Id);
        }
        catch
        {
            // ignore
        }

        var state = _likedShuffle.GetOrAdd(guild.Id, _ => new LikedShuffleState(userId));
        state.UserId = userId;
        state.ClearRecent();

        var loaded = await LoadSingleTrackAsync(like.TrackUrl);
        if (loaded.Track == null)
        {
            DisableLikedShuffle(guild.Id);
            return new PlayerActionResult(false, $"Не удалось загрузить трек из лайков: {loaded.ErrorMessage}");
        }

        state.AddRecent(like.TrackUrl);
        var trackState = new TrackState(
            loaded.Track,
            new TrackDisplay(like.Title, like.Author, like.SourceName, like.TrackUrl));
        await PlayInternalAsync(guild.Id, player, trackState);
        await NotifyPlayerStateChangedAsync(guild.Id);
        return new PlayerActionResult(true, $"Сейчас играет из лайков: **{trackState.Display.Title}**");
    }

    public async Task ShowQueueAsync(IGuild guild, ITextChannel textChannel)
    {
        _queues.TryGetValue(guild.Id, out var queue);
        var upcomingTracks = queue?.GetAllTracks() ?? new List<TrackState>();

        var hasCurrent = _currentTracks.TryGetValue(guild.Id, out var currentTrack);
        var historySnapshot = _history.TryGetValue(guild.Id, out var history) ? history.GetSnapshot() : Array.Empty<TrackState>();

        const int maxHistoryTracks = 3;
        const int maxUpcomingTracks = 5;

        var historyTracks = historySnapshot
            .Take(maxHistoryTracks)
            .Reverse()
            .ToList();

        if (upcomingTracks.Count == 0 && historyTracks.Count == 0 && !hasCurrent)
        {
            await textChannel.SendMessageAsync("Очередь пуста.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🎵 Очередь / История")
            .WithColor(Color.Blue)
            .WithFooter("Кнопки выбирают трек из очереди или истории");

        var description = string.Empty;

        for (int i = 0; i < historyTracks.Count; i++)
        {
            var track = historyTracks[i];
            var duration = track.Track.Duration.ToString(@"mm\:ss");
            var index = i - historyTracks.Count;
            description += $"({index}) **{track.Display.Title}**\n└ {track.Display.Author} • {duration}\n\n";
        }

        if (hasCurrent)
        {
            var duration = currentTrack!.Track.Duration.ToString(@"mm\:ss");
            description += $"(0) **{currentTrack.Display.Title}** (сейчас играет)\n└ {currentTrack.Display.Author} • {duration}\n\n";
        }

        var maxTracks = Math.Min(upcomingTracks.Count, maxUpcomingTracks); // Показываем максимум 5 треков с кнопками

        for (int i = 0; i < maxTracks; i++)
        {
            var track = upcomingTracks[i];
            var duration = track.Track.Duration.ToString(@"mm\:ss");
            description += $"({i + 1}) **{track.Display.Title}**\n└ {track.Display.Author} • {duration}\n\n";
        }

        if (upcomingTracks.Count > maxUpcomingTracks)
        {
            description += $"... и ещё {upcomingTracks.Count - maxUpcomingTracks} трек(ов)";
        }

        embed.WithDescription(description);

        var componentBuilder = new ComponentBuilder();
        for (int i = 0; i < historyTracks.Count; i++)
        {
            var track = historyTracks[i];
            var index = i - historyTracks.Count;
            var buttonLabel = index.ToString();
            componentBuilder.WithButton(buttonLabel, $"history_select:{guild.Id}:{index}", ButtonStyle.Secondary, emote: new Emoji("⏪"), row: 0);
        }

        for (int i = 0; i < maxTracks; i++)
        {
            var track = upcomingTracks[i];
            var title = track.Display.Title;
            var buttonLabel = title.Length > 20 ? title[..17] + "..." : title;
            componentBuilder.WithButton(buttonLabel, $"queue_select:{guild.Id}:{i}", ButtonStyle.Secondary, emote: new Emoji($"{i + 1}️⃣"), row: 1);
        }

        await textChannel.SendMessageAsync(embed: embed.Build(), components: componentBuilder.Build());
    }

    public async Task<PlayerActionResult> TrySelectHistoryTrackAsync(ulong guildId, int stepsBack)
    {
        if (stepsBack <= 0)
        {
            return new PlayerActionResult(false, "Неверный индекс трека.");
        }

        if (!_currentTracks.TryGetValue(guildId, out var currentTrack))
        {
            return new PlayerActionResult(false, "Сейчас ничего не играет.");
        }

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        if (!_history.TryGetValue(guildId, out var history))
        {
            return new PlayerActionResult(false, "Нет предыдущих треков.");
        }

        var snapshot = history.GetSnapshot();
        if (stepsBack > snapshot.Count)
        {
            return new PlayerActionResult(false, "Нет трека с таким индексом в истории.");
        }

        var selectedTrack = snapshot[stepsBack - 1];
        var moreRecent = snapshot.Take(stepsBack - 1).ToList(); // most recent -> older
        var older = snapshot.Skip(stepsBack).ToList(); // older than selected, most recent first

        history.Clear();
        for (int i = older.Count - 1; i >= 0; i--)
        {
            history.Push(older[i]);
        }

        var queue = _queues.GetOrAdd(guildId, _ => new MusicQueue());
        var forward = moreRecent.AsEnumerable().Reverse().ToList(); // chronological order after selected
        forward.Add(currentTrack);

        for (int i = forward.Count - 1; i >= 0; i--)
        {
            queue.EnqueueFront(forward[i]);
        }

        _pausedPositions.TryRemove(guildId, out _);
        await PlayInternalAsync(guildId, player, selectedTrack);
        await NotifyPlayerStateChangedAsync(guildId);
        return new PlayerActionResult(true, $"Выбран трек из истории: **{selectedTrack.Display.Title}**");
    }

    public async Task SetVolumeAsync(IGuild guild, ITextChannel textChannel, int volume)
    {
        var player = await _lavaNode.TryGetPlayerAsync(guild.Id);
        if (player == null)
        {
            await textChannel.SendMessageAsync("Я не подключен к голосовому каналу.");
            return;
        }

        await player.SetVolumeAsync(_lavaNode, volume);
        _volumes[guild.Id] = volume;
        await NotifyPlayerStateChangedAsync(guild.Id);

        await textChannel.SendMessageAsync($"Громкость: {volume}%");
    }

    public async Task<PlayerActionResult> TryPreviousAsync(ulong guildId)
    {
        CancelIdleDisconnect(guildId);

        if (!_currentTracks.ContainsKey(guildId))
        {
            return new PlayerActionResult(false, "Сейчас ничего не играет.");
        }

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        var history = _history.GetOrAdd(guildId, _ => new LimitedStack<TrackState>(25));
        if (!history.TryPop(out var previousTrack))
        {
            return new PlayerActionResult(false, "Нет предыдущего трека.");
        }

        if (_currentTracks.TryGetValue(guildId, out var currentTrack))
        {
            var queue = _queues.GetOrAdd(guildId, _ => new MusicQueue());
            queue.EnqueueFront(currentTrack);
        }

        await PlayInternalAsync(guildId, player, previousTrack);
        await NotifyPlayerStateChangedAsync(guildId);
        return new PlayerActionResult(true);
    }

    public async Task<PlayerActionResult> TrySkipAsync(ulong guildId)
    {
        CancelIdleDisconnect(guildId);

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        if (!_queues.TryGetValue(guildId, out var queue) || queue.Count == 0)
        {
            return new PlayerActionResult(false, "Очередь пуста.");
        }

        if (_currentTracks.TryGetValue(guildId, out var currentTrack))
        {
            _history.GetOrAdd(guildId, _ => new LimitedStack<TrackState>(25)).Push(currentTrack);
        }

        var nextTrack = queue.Dequeue();
        await PlayInternalAsync(guildId, player, nextTrack);
        await NotifyPlayerStateChangedAsync(guildId);
        return new PlayerActionResult(true);
    }

    public async Task<PlayerActionResult> TryTogglePauseAsync(ulong guildId)
    {
        CancelIdleDisconnect(guildId);

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        if (player.IsPaused)
        {
            // Возобновление воспроизведения
            if (_currentTracks.TryGetValue(guildId, out var currentTrack))
            {
                var usedFallback = false;
                try
                {
                    await SetPausedAsync(guildId, paused: false);
                }
                catch
                {
                    usedFallback = true;
                    await player.ResumeAsync(_lavaNode, currentTrack.Track);
                }

                // Если есть сохраненная позиция и трек поддерживает seek, переходим к ней
                if (usedFallback && _pausedPositions.TryGetValue(guildId, out var savedPosition) &&
                    currentTrack.Track.IsSeekable &&
                    savedPosition > TimeSpan.Zero &&
                    savedPosition < currentTrack.Track.Duration)
                {
                    await Task.Delay(100); // Небольшая задержка для стабильности
                    await player.SeekAsync(_lavaNode, savedPosition);
                }
            }

            _pausedPositions.TryRemove(guildId, out _);
            await NotifyPlayerStateChangedAsync(guildId);
            return new PlayerActionResult(true);
        }

        // Пауза - сохраняем текущую позицию
        _pausedPositions[guildId] = player.State.Position;
        await player.PauseAsync(_lavaNode);
        await NotifyPlayerStateChangedAsync(guildId);
        UpdateIdleDisconnectState(guildId);
        return new PlayerActionResult(true);
    }

    public async Task<PlayerActionResult> TryStopAsync(ulong guildId)
    {
        CancelIdleDisconnect(guildId);
        DisableLikedShuffle(guildId);

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        // Получаем текущий трек для остановки
        _currentTracks.TryGetValue(guildId, out _);

        // Полностью очищаем все состояния ПЕРЕД остановкой плеера
        _currentTracks.TryRemove(guildId, out _);
        _pausedPositions.TryRemove(guildId, out _);
        if (_queues.TryGetValue(guildId, out var queue))
        {
            queue.Clear();
        }

        if (_history.TryGetValue(guildId, out var history))
        {
            history.Clear();
        }

        // Полностью останавливаем плеер
        try
        {
            await StopTrackAsync(guildId);
        }
        catch
        {
            await player.PauseAsync(_lavaNode);
        }

        // Удаляем сообщение плеера из чата
        if (_playerUiService != null)
        {
            await _playerUiService.RemovePlayerMessageAsync(guildId);
        }

        await NotifyPlayerStateChangedAsync(guildId);
        UpdateIdleDisconnectState(guildId);
        return new PlayerActionResult(true);
    }

    public async Task<PlayerActionResult> TrySelectTrackAsync(ulong guildId, int trackIndex)
    {
        CancelIdleDisconnect(guildId);
        DisableLikedShuffle(guildId);

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return new PlayerActionResult(false, "Бот не подключен к голосовому каналу.");
        }

        if (!_queues.TryGetValue(guildId, out var queue) || queue.Count == 0)
        {
            return new PlayerActionResult(false, "Очередь пуста.");
        }

        var tracks = queue.GetAllTracks();
        if (trackIndex < 0 || trackIndex >= tracks.Count)
        {
            return new PlayerActionResult(false, "Неверный номер трека.");
        }

        // Перестраиваем очередь: выбранный трек становится первым, остальные следуют за ним
        var selectedTrack = tracks[trackIndex];
        var remainingTracks = tracks.Where((_, i) => i != trackIndex).ToList();

        queue.Clear();
        queue.Enqueue(selectedTrack);
        foreach (var track in remainingTracks)
        {
            queue.Enqueue(track);
        }

        // Начинаем воспроизведение выбранного трека
        await TrySkipAsync(guildId);
        return new PlayerActionResult(true, $"Выбран трек: **{selectedTrack.Display.Title}**");
    }

    private static bool IsLavalinkIdentifier(string query)
    {
        return query.StartsWith("ytsearch:", StringComparison.OrdinalIgnoreCase)
               || query.StartsWith("ytmsearch:", StringComparison.OrdinalIgnoreCase)
               || query.StartsWith("scsearch:", StringComparison.OrdinalIgnoreCase)
               || query.StartsWith("yandexmusicsearch:", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSearchIdentifier(string query, StreamingSource source)
    {
        return source switch
        {
            StreamingSource.SoundCloud => $"scsearch:{query}",
            StreamingSource.YouTubeMusic => $"ytmsearch:{query}",
            StreamingSource.YouTube => $"ytsearch:{query}",
            StreamingSource.YandexMusic => $"yandexmusicsearch:{query}",
            _ => $"ytsearch:{query}"
        };
    }

    private static string TryNormalizeYouTubeUrl(Uri uri, string original)
    {
        var host = uri.Host ?? string.Empty;
        if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            {
                var videoId = TryGetQueryParameter(uri.Query, "v");
                if (!string.IsNullOrWhiteSpace(videoId))
                {
                    return $"https://www.youtube.com/watch?v={videoId}";
                }
            }

            return original;
        }

        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var videoId = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(videoId))
            {
                return $"https://www.youtube.com/watch?v={videoId}";
            }
        }

        return original;
    }

    private static string? TryGetQueryParameter(string queryString, string name)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return null;
        }

        var query = queryString.AsSpan();
        if (query.Length > 0 && query[0] == '?')
        {
            query = query[1..];
        }

        while (!query.IsEmpty)
        {
            var amp = query.IndexOf('&');
            var pair = amp >= 0 ? query[..amp] : query;
            query = amp >= 0 ? query[(amp + 1)..] : ReadOnlySpan<char>.Empty;

            var eq = pair.IndexOf('=');
            ReadOnlySpan<char> key;
            ReadOnlySpan<char> value;
            if (eq >= 0)
            {
                key = pair[..eq];
                value = pair[(eq + 1)..];
            }
            else
            {
                key = pair;
                value = ReadOnlySpan<char>.Empty;
            }

            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(value.ToString());
                return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
            }
        }

        return null;
    }

    public bool IsLavalinkConnected() => _lavaNode.IsConnected;

    public async Task<bool> TestLavalinkConnection()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{LavalinkHost}:{LavalinkPort}/version");
            request.Headers.Add("Authorization", LavalinkPassword);

            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task PatchPlayerAsync(ulong guildId, object payload)
    {
        if (string.IsNullOrWhiteSpace(_lavaNode.SessionId))
        {
            throw new InvalidOperationException("Lavalink session is not ready.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/v{LavalinkApiVersion}/sessions/{_lavaNode.SessionId}/players/{guildId}?noReplace=false")
        {
            Content = JsonContent.Create(payload, options: LavalinkPatchJsonOptions)
        };

        using var response = await _lavalinkHttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private Task SetPausedAsync(ulong guildId, bool paused) => PatchPlayerAsync(guildId, new { paused });

    private Task StopTrackAsync(ulong guildId) => PatchPlayerAsync(guildId, new { encodedTrack = (string?)null });

    private async Task OnTrackStartAsync(TrackStartEventArg arg)
    {
        CancelIdleDisconnect(arg.GuildId);
        await NotifyPlayerStateChangedAsync(arg.GuildId);
    }

    private async Task OnTrackEndAsync(TrackEndEventArg arg)
    {
        if (arg.Reason is TrackEndReason.Finished or TrackEndReason.Load_Failed)
        {
            var state = _currentTracks.TryGetValue(arg.GuildId, out var current) ? current : new TrackState(arg.Track, TrackDisplay.From(arg.Track));
            _history.GetOrAdd(arg.GuildId, _ => new LimitedStack<TrackState>(25)).Push(state);
        }

        if (arg.Reason is TrackEndReason.Finished or TrackEndReason.Load_Failed or TrackEndReason.Stopped)
        {
            _currentTracks.TryRemove(arg.GuildId, out _);
            // При стоп также очищаем все состояния
            if (arg.Reason == TrackEndReason.Stopped)
            {
                _pausedPositions.TryRemove(arg.GuildId, out _);
                if (_queues.TryGetValue(arg.GuildId, out var stoppedQueue))
                {
                    stoppedQueue.Clear();
                }
            }
        }

        if (arg.Reason is not (TrackEndReason.Finished or TrackEndReason.Load_Failed))
        {
            await NotifyPlayerStateChangedAsync(arg.GuildId);
            UpdateIdleDisconnectState(arg.GuildId);
            return;
        }

        if (!_queues.TryGetValue(arg.GuildId, out var queue) || queue.Count == 0)
        {
            if (await TryAutoPlayLikedAsync(arg.GuildId))
            {
                return;
            }

            await NotifyPlayerStateChangedAsync(arg.GuildId);
            UpdateIdleDisconnectState(arg.GuildId);
            return;
        }

        var player = await _lavaNode.TryGetPlayerAsync(arg.GuildId);
        if (player == null)
        {
            await NotifyPlayerStateChangedAsync(arg.GuildId);
            return;
        }

        var nextTrack = queue.Dequeue();
        await PlayInternalAsync(arg.GuildId, player, nextTrack);
        await NotifyPlayerStateChangedAsync(arg.GuildId);
        UpdateIdleDisconnectState(arg.GuildId);
    }

    private async Task<bool> TryAutoPlayLikedAsync(ulong guildId)
    {
        if (!_likedShuffle.TryGetValue(guildId, out var state) || !_likesService.IsEnabled)
        {
            return false;
        }

        var player = await _lavaNode.TryGetPlayerAsync(guildId);
        if (player == null)
        {
            return false;
        }

        var gate = _likedShuffleLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_likedShuffle.TryGetValue(guildId, out state))
            {
                return false;
            }

            // Пытаемся несколько раз, чтобы не залипнуть на одном битом URL
            var exclude = state.GetRecentSnapshot();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var like = await _likesService.GetRandomLikeAsync(guildId, state.UserId, exclude);
                if (like == null)
                {
                    DisableLikedShuffle(guildId);
                    return false;
                }

                var loaded = await LoadSingleTrackAsync(like.TrackUrl);
                if (loaded.Track != null)
                {
                    state.AddRecent(like.TrackUrl);
                    var trackState = new TrackState(
                        loaded.Track,
                        new TrackDisplay(like.Title, like.Author, like.SourceName, like.TrackUrl));
                    await PlayInternalAsync(guildId, player, trackState);
                    await NotifyPlayerStateChangedAsync(guildId);
                    return true;
                }

                exclude = exclude.Append(like.TrackUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            DisableLikedShuffle(guildId);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnqueueYandexPlaylistAsync(
        IGuild guild,
        IVoiceChannel voiceChannel,
        ITextChannel textChannel,
        LavaPlayer<LavaTrack> player,
        string user,
        string kind)
    {
        if (!_yandexMusicService.IsEnabled)
        {
            await textChannel.SendMessageAsync("Yandex Music не настроен. Добавьте YANDEX_MUSIC_TOKEN в переменные окружения.");
            return;
        }

        var (playlistTitle, ymTracks) = await _yandexMusicService.GetPlaylistAsync(user, kind);
        if (ymTracks.Count == 0)
        {
            await textChannel.SendMessageAsync("Не удалось получить треки плейлиста Yandex Music.");
            return;
        }

        const int maxPlaylistTracks = 50;
        var total = ymTracks.Count;
        if (ymTracks.Count > maxPlaylistTracks)
        {
            ymTracks = ymTracks.Take(maxPlaylistTracks).ToList();
        }

        var concurrency = new SemaphoreSlim(3, 3);
        var results = new TrackState?[ymTracks.Count];

        var tasks = ymTracks.Select(async (ymTrack, index) =>
        {
            await concurrency.WaitAsync();
            try
            {
                var downloadUrl = await _yandexMusicService.GetDownloadUrlAsync(ymTrack);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    return;
                }

                var loaded = await LoadSingleTrackAsync(downloadUrl);
                if (loaded.Track == null)
                {
                    return;
                }

                var author = ymTrack.Artists.Count > 0 ? string.Join(", ", ymTrack.Artists) : "Yandex Music";
                var url = _yandexMusicService.GetTrackUrl(ymTrack);
                var display = new TrackDisplay(ymTrack.Title, author, "Yandex Music", url);
                results[index] = new TrackState(loaded.Track, display);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var playlistItems = results.Where(x => x != null).Select(x => x!).ToList();
        if (playlistItems.Count == 0)
        {
            await textChannel.SendMessageAsync("Не удалось загрузить ни одного трека из плейлиста Yandex Music.");
            return;
        }

        var queue = _queues.GetOrAdd(guild.Id, _ => new MusicQueue());
        var titleSuffix = string.IsNullOrWhiteSpace(playlistTitle) ? string.Empty : $": **{playlistTitle}**";
        var truncatedSuffix = total > ymTracks.Count ? $"\nОграничено: добавлено {ymTracks.Count} из {total}." : string.Empty;

        if (_currentTracks.ContainsKey(guild.Id))
        {
            foreach (var item in playlistItems)
            {
                queue.Enqueue(item);
            }

            await NotifyPlayerStateChangedAsync(guild.Id);
            await textChannel.SendMessageAsync($"Добавлено **{playlistItems.Count}** трек(ов) из плейлиста Yandex Music{titleSuffix} в очередь.{truncatedSuffix}");
            return;
        }

        for (var i = 1; i < playlistItems.Count; i++)
        {
            queue.Enqueue(playlistItems[i]);
        }

        await PlayInternalAsync(guild.Id, player, playlistItems[0]);
        await NotifyPlayerStateChangedAsync(guild.Id);
        await textChannel.SendMessageAsync($"Сейчас играет (Yandex Music): **{playlistItems[0].Display.Title}**\nДобавлено в очередь: **{playlistItems.Count - 1}**{truncatedSuffix}");
    }

    private sealed record LoadedTrackResult(LavaTrack? Track, string? ErrorMessage);

    private async Task<LoadedTrackResult> LoadSingleTrackAsync(string identifier)
    {
        SearchResponse searchResponse;
        try
        {
            searchResponse = await _lavaNode.LoadTrackAsync(identifier);
        }
        catch (Exception ex)
        {
            return new LoadedTrackResult(null, ex.Message);
        }

        if (searchResponse == null)
        {
            return new LoadedTrackResult(null, "Lavalink вернул пустой ответ.");
        }

        if (searchResponse.Type == SearchType.Error)
        {
            var details = searchResponse.Exception.Message?.Trim();
            if (string.IsNullOrWhiteSpace(details))
            {
                details = searchResponse.Exception.Cause?.Trim();
            }

            details ??= "Ошибка Lavalink.";
            return new LoadedTrackResult(null, details);
        }

        var tracks = searchResponse.Tracks;
        if (searchResponse.Type == SearchType.Empty || tracks == null || tracks.Count == 0)
        {
            return new LoadedTrackResult(null, "Ничего не найдено.");
        }

        return new LoadedTrackResult(tracks.First(), null);
    }

    private async Task PlayInternalAsync(ulong guildId, LavaPlayer<LavaTrack> player, TrackState track)
    {
        CancelIdleDisconnect(guildId);

        var volume = _volumes.TryGetValue(guildId, out var existingVolume) ? existingVolume : 100;
        await player.PlayAsync(_lavaNode, track.Track, false, volume, false);
        _currentTracks[guildId] = track;
    }

    private void UpdateIdleDisconnectState(ulong guildId)
    {
        if (!IsIdle(guildId))
        {
            CancelIdleDisconnect(guildId);
            return;
        }

        RequestIdleDisconnect(guildId);
    }

    private bool IsIdle(ulong guildId)
    {
        if (!_voiceChannelIds.ContainsKey(guildId))
        {
            return false;
        }

        // Если трек стоит на паузе, считаем это "не играет" и разрешаем авто-выход.
        var hasCurrentTrack = _currentTracks.ContainsKey(guildId) && !_pausedPositions.ContainsKey(guildId);
        if (hasCurrentTrack)
        {
            return false;
        }

        if (_queues.TryGetValue(guildId, out var queue) && queue.Count > 0)
        {
            return false;
        }

        return true;
    }

    private void RequestIdleDisconnect(ulong guildId)
    {
        var cts = new CancellationTokenSource();
        _idleDisconnectTokens.AddOrUpdate(guildId, cts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return cts;
        });

        _ = RunIdleDisconnectAsync(guildId, cts);
    }

    private void CancelIdleDisconnect(ulong guildId)
    {
        if (_idleDisconnectTokens.TryRemove(guildId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }

    private async Task RunIdleDisconnectAsync(ulong guildId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(IdleDisconnectDelay, cts.Token);

            if (!IsIdle(guildId))
            {
                return;
            }

            await DisconnectIdleAsync(guildId);
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            // ignore idle disconnect failures
        }
        finally
        {
            if (_idleDisconnectTokens.TryGetValue(guildId, out var current) && ReferenceEquals(current, cts))
            {
                _idleDisconnectTokens.TryRemove(guildId, out _);
            }

            cts.Dispose();
        }
    }

    private async Task DisconnectIdleAsync(ulong guildId)
    {
        CancelIdleDisconnect(guildId);
        DisableLikedShuffle(guildId);

        var disconnected = false;
        if (_voiceChannels.TryGetValue(guildId, out var voiceChannel))
        {
            try
            {
                await _lavaNode.LeaveAsync(voiceChannel);
                disconnected = true;
            }
            catch
            {
                // ignore leave failures
            }
        }

        try
        {
            await _lavaNode.DestroyPlayerAsync(guildId);
        }
        catch
        {
            // ignore destroy failures
        }

        _currentTracks.TryRemove(guildId, out _);
        _pausedPositions.TryRemove(guildId, out _);

        if (disconnected)
        {
            _voiceChannelIds.TryRemove(guildId, out _);
            _voiceChannels.TryRemove(guildId, out _);
        }

        if (_queues.TryGetValue(guildId, out var queue))
        {
            queue.Clear();
        }

        if (_history.TryGetValue(guildId, out var history))
        {
            history.Clear();
        }

        if (_playerUiService != null)
        {
            await _playerUiService.RemovePlayerMessageAsync(guildId);
        }

        await NotifyPlayerStateChangedAsync(guildId);

        // Если по какой-то причине не удалось выйти из войса, попробуем снова позже.
        if (_voiceChannelIds.ContainsKey(guildId))
        {
            UpdateIdleDisconnectState(guildId);
        }
    }

    private sealed class LikedShuffleState
    {
        private const int MaxRecent = 5;

        private readonly object _gate = new();
        private readonly Queue<string> _recent = new();
        private readonly HashSet<string> _recentSet = new(StringComparer.OrdinalIgnoreCase);

        public LikedShuffleState(ulong userId)
        {
            UserId = userId;
        }

        public ulong UserId { get; set; }

        public void AddRecent(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            lock (_gate)
            {
                if (_recentSet.Contains(url))
                {
                    return;
                }

                _recent.Enqueue(url);
                _recentSet.Add(url);

                while (_recent.Count > MaxRecent)
                {
                    var removed = _recent.Dequeue();
                    _recentSet.Remove(removed);
                }
            }
        }

        public void ClearRecent()
        {
            lock (_gate)
            {
                _recent.Clear();
                _recentSet.Clear();
            }
        }

        public string[] GetRecentSnapshot()
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    private async Task NotifyPlayerStateChangedAsync(ulong guildId)
    {
        var handlers = PlayerStateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().OfType<Func<ulong, Task>>())
        {
            try
            {
                await handler(guildId);
            }
            catch
            {
                // ignore handler failures
            }
        }
    }
}

public sealed record TrackDisplay(string Title, string Author, string? SourceName, string Url)
{
    public static TrackDisplay From(LavaTrack track)
    {
        var title = string.IsNullOrWhiteSpace(track.Title) ? "Unknown title" : track.Title;
        var author = track.Author ?? string.Empty;
        var sourceName = track.SourceName;
        var url = track.Url ?? string.Empty;
        return new TrackDisplay(title, author, sourceName, url);
    }
}

public sealed record TrackState(LavaTrack Track, TrackDisplay Display);

public class MusicQueue
{
    private readonly LinkedList<TrackState> _tracks = new();

    public int Count => _tracks.Count;

    public void Enqueue(TrackState track) => _tracks.AddLast(track);

    public void EnqueueFront(TrackState track) => _tracks.AddFirst(track);

    public TrackState Dequeue()
    {
        var first = _tracks.First;
        if (first == null)
        {
            throw new InvalidOperationException("Queue is empty.");
        }

        _tracks.RemoveFirst();
        return first.Value;
    }

    public void Clear() => _tracks.Clear();

    public List<TrackState> GetAllTracks() => _tracks.ToList();
}
