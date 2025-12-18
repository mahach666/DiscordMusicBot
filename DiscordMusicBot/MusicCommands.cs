using Discord;
using Discord.Commands;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordMusicBot;

public class MusicCommands : ModuleBase<SocketCommandContext>
{
    private readonly AudioService _audioService;
    private readonly PlayerUiService _playerUiService;
    private readonly StreamingPreferencesService _streamingPreferences;
    private readonly LikesService _likesService;
    private readonly YouTubeService _youTubeService;

    public MusicCommands(
        AudioService audioService,
        PlayerUiService playerUiService,
        StreamingPreferencesService streamingPreferences,
        LikesService likesService,
        IServiceProvider services)
    {
        _audioService = audioService;
        _playerUiService = playerUiService;
        _streamingPreferences = streamingPreferences;
        _likesService = likesService;

        // Инициализация YouTube API
        var config = services.GetRequiredService<Config>();
        _youTubeService = new YouTubeService(new BaseClientService.Initializer
        {
            ApiKey = config.YouTubeApiKey,
            ApplicationName = "DiscordMusicBot"
        });
    }

    [Command("join")]
    [Alias("j")]
    [Summary("Подключиться к голосовому каналу")]
    public async Task JoinAsync()
    {
        var voiceChannel = (Context.User as IGuildUser)?.VoiceChannel;
        if (voiceChannel == null)
        {
            await ReplyAsync("Вы должны быть в голосовом канале!");
            return;
        }

        await _audioService.JoinAsync(voiceChannel, Context.Channel as ITextChannel);
    }

    [Command("leave")]
    [Alias("l")]
    [Summary("Отключиться от голосового канала")]
    public async Task LeaveAsync()
    {
        await _audioService.LeaveAsync(Context.Guild, Context.Channel as ITextChannel);
    }

    [Command("player")]
    [Alias("controls", "ui")]
    [Summary("Show player controls")]
    public async Task PlayerAsync()
    {
        if (Context.Channel is not ITextChannel textChannel)
        {
            await ReplyAsync("This command works in text channels.");
            return;
        }

        await _playerUiService.ShowAsync(Context.Guild, textChannel);
    }

    [Command("source")]
    [Alias("service", "provider", "src")]
    [Summary("Установить приоритетный сервис для поиска (auto/youtube/ytmusic/soundcloud/yandexmusic)")]
    public async Task SourceAsync(string? source = null)
    {
        if (Context.Guild == null)
        {
            await ReplyAsync("Команда доступна только на сервере.");
            return;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            var current = await _streamingPreferences.GetPreferredSourceAsync(Context.Guild.Id);
            await ReplyAsync($"Приоритетный сервис: **{FormatSource(current)}**\nДоступно: `auto`, `youtube`, `ytmusic`, `soundcloud`, `yandexmusic`");
            return;
        }

        if (!TryParseSource(source, out var parsed))
        {
            await ReplyAsync("Неверное значение. Доступно: `auto`, `youtube`, `ytmusic`, `soundcloud`, `yandexmusic`");
            return;
        }

        await _streamingPreferences.SetPreferredSourceAsync(Context.Guild.Id, parsed);
        await ReplyAsync($"Приоритетный сервис установлен: **{FormatSource(parsed)}**");
    }

    [Command("play")]
    [Alias("p")]
    [Summary("Воспроизвести музыку по названию или URL")]
    public async Task PlayAsync([Remainder] string query)
    {
        var voiceChannel = (Context.User as IGuildUser)?.VoiceChannel;
        if (voiceChannel == null)
        {
            await ReplyAsync("Вы должны быть в голосовом канале!");
            return;
        }

        if (Context.Channel is not ITextChannel textChannel)
        {
            await ReplyAsync("Эта команда доступна только в текстовом канале сервера.");
            return;
        }

        await _audioService.PlayAsync(query, Context.Guild, voiceChannel, textChannel);
        await _playerUiService.TryBumpAfterPlayCommandAsync(Context.Guild, textChannel);
    }

    [Command("like")]
    [Alias("fav")]
    [Summary("Добавить текущий трек в лайки")]
    public async Task LikeAsync()
    {
        if (Context.Guild == null)
        {
            await ReplyAsync("Команда доступна только на сервере.");
            return;
        }

        if (!_audioService.TryGetCurrentTrackState(Context.Guild.Id, out var track))
        {
            await ReplyAsync("Сейчас ничего не играет.");
            return;
        }

        var result = await _likesService.LikeAsync(Context.Guild.Id, Context.User.Id, track);
        await ReplyAsync(result.Message);
    }

    [Command("unlike")]
    [Alias("unfav")]
    [Summary("Удалить текущий трек из лайков")]
    public async Task UnlikeAsync()
    {
        if (Context.Guild == null)
        {
            await ReplyAsync("Команда доступна только на сервере.");
            return;
        }

        if (!_audioService.TryGetCurrentTrackState(Context.Guild.Id, out var track))
        {
            await ReplyAsync("Сейчас ничего не играет.");
            return;
        }

        var result = await _likesService.UnlikeAsync(Context.Guild.Id, Context.User.Id, track);
        await ReplyAsync(result.Message);
    }

    [Command("likes")]
    [Alias("favs")]
    [Summary("Показать лайки / включить случайное воспроизведение лайков")]
    public async Task LikesAsync([Remainder] string? args = null)
    {
        if (Context.Guild == null)
        {
            await ReplyAsync("Команда доступна только на сервере.");
            return;
        }

        args = args?.Trim();

        if (string.IsNullOrWhiteSpace(args))
        {
            await ShowLikesAsync();
            return;
        }

        if (args.Equals("shuffle", StringComparison.OrdinalIgnoreCase)
            || args.Equals("random", StringComparison.OrdinalIgnoreCase)
            || args.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            var voiceChannel = (Context.User as IGuildUser)?.VoiceChannel;
            if (voiceChannel == null)
            {
                await ReplyAsync("Вы должны быть в голосовом канале!");
                return;
            }

            if (Context.Channel is not ITextChannel textChannel)
            {
                await ReplyAsync("Эта команда доступна только в текстовом канале сервера.");
                return;
            }

            var result = await _audioService.StartLikedShuffleAsync(Context.Guild, voiceChannel, textChannel, Context.User.Id);
            await ReplyAsync(result.Message);
            return;
        }

        if (args.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || args.Equals("off", StringComparison.OrdinalIgnoreCase)
            || args.Equals("disable", StringComparison.OrdinalIgnoreCase))
        {
            _audioService.DisableLikedShuffle(Context.Guild.Id);
            await ReplyAsync("Режим лайков выключен.");
            return;
        }

        if (int.TryParse(args, out var index))
        {
            await PlayLikeByIndexAsync(index);
            return;
        }

        await ReplyAsync("Использование: `!likes` | `!likes shuffle` | `!likes stop` | `!likes <номер>`");
    }

    private async Task ShowLikesAsync()
    {
        if (!_likesService.IsEnabled)
        {
            await ReplyAsync("База данных не настроена. Лайки недоступны без Postgres.");
            return;
        }

        var userId = Context.User.Id;
        var guildId = Context.Guild.Id;

        var likes = await _likesService.GetLikesAsync(guildId, userId, limit: 10);
        if (likes.Count == 0)
        {
            await ReplyAsync("У вас пока нет лайков. Поставьте лайк текущему треку: `!like`");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("❤️ Ваши лайки")
            .WithColor(Color.Gold);

        if (_audioService.TryGetLikedShuffleUserId(guildId, out var shuffleUserId) && shuffleUserId == userId)
        {
            embed.WithDescription("Режим лайков: **включен** (`!likes stop`)");
        }
        else
        {
            embed.WithDescription("Режим лайков: **выключен** (`!likes shuffle`)");
        }

        var lines = new List<string>();
        for (var i = 0; i < likes.Count; i++)
        {
            var like = likes[i];
            var duration = like.Duration.ToString(@"mm\:ss");
            var title = string.IsNullOrWhiteSpace(like.Title) ? like.TrackUrl : like.Title;
            lines.Add($"{i + 1}. [{title}]({like.TrackUrl})\n   {like.Author} • {duration}");
        }

        embed.AddField("Треки", string.Join("\n\n", lines));

        var components = new ComponentBuilder()
            .WithButton("Shuffle", $"likes_shuffle:{guildId}:{userId}", ButtonStyle.Success, emote: new Emoji("🔀"), row: 0)
            .WithButton("Stop", $"likes_stop:{guildId}:{userId}", ButtonStyle.Secondary, emote: new Emoji("⏹"), row: 0);

        for (var i = 0; i < likes.Count; i++)
        {
            var like = likes[i];
            var row = 1 + (i / 5);
            components.WithButton((i + 1).ToString(), $"likes_play:{guildId}:{userId}:{like.Id}", ButtonStyle.Primary, emote: new Emoji("▶"), row: row);
        }

        await Context.Channel.SendMessageAsync(embed: embed.Build(), components: components.Build());
    }

    private async Task PlayLikeByIndexAsync(int index)
    {
        if (!_likesService.IsEnabled)
        {
            await ReplyAsync("База данных не настроена. Лайки недоступны без Postgres.");
            return;
        }

        if (index <= 0)
        {
            await ReplyAsync("Номер должен быть >= 1.");
            return;
        }

        var voiceChannel = (Context.User as IGuildUser)?.VoiceChannel;
        if (voiceChannel == null)
        {
            await ReplyAsync("Вы должны быть в голосовом канале!");
            return;
        }

        if (Context.Channel is not ITextChannel textChannel)
        {
            await ReplyAsync("Эта команда доступна только в текстовом канале сервера.");
            return;
        }

        var likes = await _likesService.GetLikesAsync(Context.Guild.Id, Context.User.Id, limit: index);
        if (likes.Count < index)
        {
            await ReplyAsync("Нет трека с таким номером в списке.");
            return;
        }

        var like = likes[index - 1];
        var result = await _audioService.PlayLikedAsync(Context.Guild, voiceChannel, textChannel, Context.User.Id, like.Id);
        await ReplyAsync(result.Message);
    }

    [Command("search")]
    [Alias("s")]
    [Summary("Поиск музыки на YouTube")]
    public async Task SearchAsync([Remainder] string query)
    {
        var results = await SearchYouTubeMultipleAsync(query, 5);
        if (results.Count == 0)
        {
            await ReplyAsync("Ничего не найдено!");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Результаты поиска: {query}")
            .WithColor(Color.Green);

        var description = "";
        for (int i = 0; i < results.Count; i++)
        {
            var video = results[i];
            description += $"{i + 1}. **{video.Title}**\n{video.ChannelTitle}\nДлительность: {video.Duration}\n\n";
        }

        description += "Используйте `!play <номер>` чтобы выбрать трек или `!play <название>` для поиска.";

        embed.WithDescription(description);
        await ReplyAsync(embed: embed.Build());
    }

    [Command("skip")]
    [Alias("next", "n")]
    [Summary("Пропустить текущий трек")]
    public async Task SkipAsync()
    {
        await _audioService.SkipAsync(Context.Guild, Context.Channel as ITextChannel);
    }

    [Command("pause")]
    [Alias("resume")]
    [Summary("Приостановить/возобновить воспроизведение")]
    public async Task PauseAsync()
    {
        await _audioService.PauseAsync(Context.Guild, Context.Channel as ITextChannel);
    }

    [Command("stop")]
    [Summary("Остановить воспроизведение и очистить очередь")]
    public async Task StopAsync()
    {
        await _audioService.StopAsync(Context.Guild, Context.Channel as ITextChannel);
    }

    [Command("queue")]
    [Alias("q")]
    [Summary("Показать очередь воспроизведения")]
    public async Task ShowQueueAsync()
    {
        await _audioService.ShowQueueAsync(Context.Guild, Context.Channel as ITextChannel);
    }

    [Command("volume")]
    [Alias("vol", "v")]
    [Summary("Установить громкость (0-100)")]
    public async Task SetVolumeAsync(int volume)
    {
        if (volume < 0 || volume > 100)
        {
            await ReplyAsync("Громкость должна быть от 0 до 100!");
            return;
        }

        await _audioService.SetVolumeAsync(Context.Guild, Context.Channel as ITextChannel, volume);
    }

    [Command("status")]
    [Alias("stat", "info")]
    [Summary("Показать статус бота и Lavalink")]
    public async Task StatusAsync()
    {
        var embed = new EmbedBuilder()
            .WithTitle("📊 Статус бота")
            .WithColor(Color.Green);

        // Проверяем Lavalink
        bool lavalinkConnected = _audioService.IsLavalinkConnected();
        bool lavalinkResponds = await _audioService.TestLavalinkConnection();

        embed.AddField("🤖 Discord Bot",
            $"✅ Подключен к {Context.Client.Guilds.Count} серверу(ам)\n" +
            $"📡 Задержка: {Context.Client.Latency}ms");

        embed.AddField("🎵 Lavalink Server",
            $"{(lavalinkConnected ? "🟢" : "🔴")} WebSocket: {(lavalinkConnected ? "Подключен" : "Не подключен")}\n" +
            $"{(lavalinkResponds ? "🟢" : "🔴")} HTTP API: {(lavalinkResponds ? "Отвечает" : "Не отвечает")}\n" +
            $"📍 Адрес: 127.0.0.1:2333");

        if (!lavalinkConnected && lavalinkResponds)
        {
            embed.AddField("⚠️ Рекомендация",
                "Lavalink отвечает на HTTP, но WebSocket не подключен.\n" +
                "Попробуйте подождать или перезапустить Lavalink.");
        }
        else if (!lavalinkResponds)
        {
            embed.AddField("❌ Проблема",
                "Lavalink не отвечает!\n" +
                "Убедитесь, что Lavalink.jar запущен на порту 2333.");
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("help")]
    [Alias("h")]
    [Summary("Показать справку по командам")]
    public async Task HelpAsync()
    {
        var embed = new EmbedBuilder()
            .WithTitle("🎵 Справка по командам")
            .WithColor(Color.Blue)
            .WithDescription("Доступные команды для музыкального бота:")
            .AddField("🎶 Основные команды:",
                "`!join` или `!j` - подключиться к голосовому каналу\n" +
                "`!leave` или `!l` - отключиться от голосового канала\n" +
                "`!play <название/URL>` или `!p` - воспроизвести музыку\n" +
                "`!search <запрос>` или `!s` - поиск музыки на YouTube")
            .AddField("🎛️ Управление воспроизведением:",
                "`!skip` или `!next` - пропустить трек\n" +
                "`!pause` или `!resume` - пауза/возобновление\n" +
                "`!stop` - остановить и очистить очередь\n" +
                "`!volume <0-100>` или `!vol` - установить громкость")
            .AddField("🔎 Источник поиска:",
                "`!source` - показать текущий источник\n" +
                "`!source auto|youtube|ytmusic|soundcloud|yandexmusic` - установить приоритетный сервис")
            .AddField("❤️ Лайки:",
                "`!like` - добавить текущий трек в лайки\n" +
                "`!unlike` - удалить текущий трек из лайков\n" +
                "`!likes` - показать лайки (с кнопками)\n" +
                "`!likes shuffle` - включить случайное проигрывание лайков\n" +
                "`!likes stop` - выключить режим лайков")
            .AddField("📊 Информация:",
                "`!queue` или `!q` - показать очередь с кнопками выбора трека\n" +
                "`!status` или `!stat` - статус бота и Lavalink\n" +
                "`!help` или `!h` - эта справка");

        await ReplyAsync(embed: embed.Build());
    }

    private static bool TryParseSource(string value, out StreamingSource source)
    {
        source = StreamingSource.Auto;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" => (source = StreamingSource.Auto) == StreamingSource.Auto,
            "youtube" or "yt" => (source = StreamingSource.YouTube) == StreamingSource.YouTube,
            "ytmusic" or "ytm" or "music" => (source = StreamingSource.YouTubeMusic) == StreamingSource.YouTubeMusic,
            "soundcloud" or "sc" => (source = StreamingSource.SoundCloud) == StreamingSource.SoundCloud,
            "yandexmusic" or "yandex" or "ym" => (source = StreamingSource.YandexMusic) == StreamingSource.YandexMusic,
            _ => false
        };
    }

    private static string FormatSource(StreamingSource source)
    {
        return source switch
        {
            StreamingSource.SoundCloud => "SoundCloud",
            StreamingSource.YouTubeMusic => "YouTube Music",
            StreamingSource.YouTube => "YouTube",
            StreamingSource.YandexMusic => "Yandex Music",
            _ => "Auto (YouTube → SoundCloud)"
        };
    }


    private async Task<string> SearchYouTubeAsync(string query)
    {
        try
        {
            var searchRequest = _youTubeService.Search.List("snippet");
            searchRequest.Q = query;
            searchRequest.MaxResults = 1;
            searchRequest.Type = "video";
            searchRequest.VideoCategoryId = "10"; // Music category

            var searchResponse = await searchRequest.ExecuteAsync();

            if (searchResponse.Items.Count > 0)
            {
                var videoId = searchResponse.Items[0].Id.VideoId;
                return $"https://www.youtube.com/watch?v={videoId}";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка поиска на YouTube: {ex.Message}");
        }

        return string.Empty;
    }

    private async Task<List<YouTubeVideoInfo>> SearchYouTubeMultipleAsync(string query, int maxResults)
    {
        var results = new List<YouTubeVideoInfo>();

        try
        {
            var searchRequest = _youTubeService.Search.List("snippet");
            searchRequest.Q = query;
            searchRequest.MaxResults = maxResults;
            searchRequest.Type = "video";
            searchRequest.VideoCategoryId = "10"; // Music category

            var searchResponse = await searchRequest.ExecuteAsync();

            // Получаем детали видео для длительности
            var videoIds = searchResponse.Items.Select(item => item.Id.VideoId).ToList();
            var videoRequest = _youTubeService.Videos.List("contentDetails,snippet");
            videoRequest.Id = string.Join(",", videoIds);

            var videoResponse = await videoRequest.ExecuteAsync();

            foreach (var video in videoResponse.Items)
            {
                var searchItem = searchResponse.Items.FirstOrDefault(s => s.Id.VideoId == video.Id);
                if (searchItem != null)
                {
                    results.Add(new YouTubeVideoInfo
                    {
                        Title = video.Snippet.Title,
                        ChannelTitle = video.Snippet.ChannelTitle,
                        VideoId = video.Id,
                        Duration = ParseDuration(video.ContentDetails.Duration)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка поиска на YouTube: {ex.Message}");
        }

        return results;
    }

    private string ParseDuration(string duration)
    {
        // Парсинг ISO 8601 duration (PT4M13S -> 4:13)
        if (string.IsNullOrEmpty(duration)) return "N/A";

        duration = duration.Replace("PT", "");

        var hours = 0;
        var minutes = 0;
        var seconds = 0;

        if (duration.Contains("H"))
        {
            var parts = duration.Split('H');
            hours = int.Parse(parts[0]);
            duration = parts[1];
        }

        if (duration.Contains("M"))
        {
            var parts = duration.Split('M');
            minutes = int.Parse(parts[0]);
            duration = parts[1];
        }

        if (duration.Contains("S"))
        {
            var parts = duration.Split('S');
            seconds = int.Parse(parts[0]);
        }

        if (hours > 0)
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        else
            return $"{minutes}:{seconds:D2}";
    }
}

public class YouTubeVideoInfo
{
    public string Title { get; set; } = null!;
    public string ChannelTitle { get; set; } = null!;
    public string VideoId { get; set; } = null!;
    public string Duration { get; set; } = null!;
}
