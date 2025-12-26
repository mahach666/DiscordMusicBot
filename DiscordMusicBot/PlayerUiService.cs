using Discord;
using Discord.WebSocket;
using System.Collections.Concurrent;
using Victoria;

namespace DiscordMusicBot;

public sealed class PlayerUiService
{
    private const string CustomIdPrefix = "player";
    private static readonly TimeSpan TrackChangeBumpDelay = TimeSpan.FromMilliseconds(750);

    private readonly DiscordSocketClient _client;
    private readonly AudioService _audioService;
    private readonly LikesService _likesService;
    private readonly LavaNode _lavaNode;
    private readonly ConcurrentDictionary<ulong, PlayerMessageRef> _playerMessages = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _updateLocks = new();
    private readonly ConcurrentDictionary<ulong, string?> _lastTrackHashes = new();
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _trackChangeBumpTokens = new();

    public PlayerUiService(DiscordSocketClient client, AudioService audioService, LikesService likesService, LavaNode lavaNode)
    {
        _client = client;
        _audioService = audioService;
        _likesService = likesService;
        _lavaNode = lavaNode;

        _audioService.PlayerStateChanged += UpdatePlayerMessageAsync;
    }

    public Task TryBumpAfterPlayCommandAsync(IGuild guild, ITextChannel channel)
    {
        return TryBumpAsync(guild.Id, channel.Id);
    }

    private async Task TryBumpAsync(ulong guildId, ulong channelId)
    {
        if (!_playerMessages.TryGetValue(guildId, out var messageRef))
        {
            return;
        }

        if (messageRef.ChannelId != channelId)
        {
            return;
        }

        CancelTrackChangeBump(guildId);
        await BumpPlayerMessageAsync(guildId, requireTrack: false);
    }

    public async Task<IUserMessage?> ShowAsync(IGuild guild, ITextChannel channel)
    {
        CancelTrackChangeBump(guild.Id);
        var (embed, components) = await BuildPlayerMessageAsync(guild.Id);

        // Всегда удаляем старое сообщение плеера и создаем новое внизу чата
        if (_playerMessages.TryGetValue(guild.Id, out var existing))
        {
            try
            {
                if (_client.GetChannel(existing.ChannelId) is IMessageChannel existingChannel)
                {
                    var existingMessage = await existingChannel.GetMessageAsync(existing.MessageId);
                    if (existingMessage is IUserMessage userMessage)
                    {
                        await userMessage.DeleteAsync();
                    }
                }
            }
            catch
            {
                // ignore deletion failures
            }
            finally
            {
                _playerMessages.TryRemove(guild.Id, out _);
            }
        }

        // Создаем новое сообщение внизу чата
        var sent = await channel.SendMessageAsync(embed: embed, components: components);
        _playerMessages[guild.Id] = new PlayerMessageRef(channel.Id, sent.Id);
        _lastTrackHashes[guild.Id] = GetCurrentTrackHash(guild.Id);
        return sent;
    }

    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;

        if (customId.StartsWith("likes_like:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLikesLikeAsync(component);
            return;
        }

        if (customId.StartsWith("likes_page:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLikesPageAsync(component);
            return;
        }

        if (customId.StartsWith("likes_shuffle:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLikesShuffleAsync(component);
            return;
        }

        if (customId.StartsWith("likes_stop:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLikesStopAsync(component);
            return;
        }

        if (customId.StartsWith("likes_play:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLikesPlayAsync(component);
            return;
        }

        // Обработка кнопок очереди
        if (customId.StartsWith("queue_select:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueueSelectAsync(component);
            return;
        }

        if (customId.StartsWith("history_select:", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHistorySelectAsync(component);
            return;
        }

        // Обработка кнопок плеера
        if (!TryParseCustomId(customId, out var guildId, out var action))
        {
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (!_audioService.TryGetVoiceChannelId(guildId, out var botVoiceChannelId))
        {
            await component.RespondAsync("Бот не подключен к голосовому каналу.", ephemeral: true);
            return;
        }

        if (component.User is not SocketGuildUser guildUser || guildUser.VoiceChannel?.Id != botVoiceChannelId)
        {
            await component.RespondAsync("Зайди в тот же голосовой канал, что и бот, чтобы управлять плеером.", ephemeral: true);
            return;
        }

        _playerMessages[guildId] = new PlayerMessageRef(component.Channel.Id, component.Message.Id);

        await component.DeferAsync();

        PlayerActionResult result = action switch
        {
            "prev" => await _audioService.TryPreviousAsync(guildId),
            "pause" => await _audioService.TryTogglePauseAsync(guildId),
            "next" => await _audioService.TrySkipAsync(guildId),
            "stop" => await _audioService.TryStopAsync(guildId),
            _ => new PlayerActionResult(false, "Неизвестное действие.")
        };

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))
        {
            await component.FollowupAsync(result.Message, ephemeral: true);
        }
    }

    public async Task<(Embed embed, MessageComponent components)> BuildLikesMessageAsync(ulong guildId, ulong userId, int page)
    {
        if (!_likesService.IsEnabled)
        {
            var disabledEmbed = new EmbedBuilder()
                .WithTitle("❤️ Ваши лайки")
                .WithColor(Color.DarkGrey)
                .WithDescription("База данных не настроена. Лайки недоступны без Postgres.")
                .Build();
            return (disabledEmbed, new ComponentBuilder().Build());
        }

        const int pageSize = 10;
        var totalCount = await _likesService.GetLikesCountAsync(guildId, userId);
        if (totalCount <= 0)
        {
            var emptyEmbed = new EmbedBuilder()
                .WithTitle("❤️ Ваши лайки")
                .WithColor(Color.Gold)
                .WithDescription("У вас пока нет лайков. Поставьте лайк текущему треку: `!like`")
                .Build();
            return (emptyEmbed, new ComponentBuilder().Build());
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var offset = (page - 1) * pageSize;

        var likes = await _likesService.GetLikesAsync(guildId, userId, limit: pageSize, offset: offset);

        var embedBuilder = new EmbedBuilder()
            .WithTitle("❤️ Ваши лайки")
            .WithColor(Color.Gold);

        var likedShuffleOn = _audioService.TryGetLikedShuffleUserId(guildId, out var shuffleUserId) && shuffleUserId == userId;
        var header = likedShuffleOn
            ? "Режим лайков: **включен** (`!likes stop`)"
            : "Режим лайков: **выключен** (`!likes shuffle`)";

        static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
        }

        var descriptionLines = new List<string>
        {
            header,
            $"Страница: **{page}/{totalPages}** • Всего: **{totalCount}**",
            "Используй кнопки `◀/▶` для листания, или `!likes <номер>` чтобы включить трек по номеру.",
            string.Empty
        };

        for (var i = 0; i < likes.Count; i++)
        {
            var like = likes[i];
            var duration = like.Duration.ToString(@"mm\:ss");
            var title = string.IsNullOrWhiteSpace(like.Title) ? like.TrackUrl : like.Title;
            title = Truncate(title, 90);
            var author = Truncate(like.Author ?? string.Empty, 40);
            var index = offset + i + 1;
            descriptionLines.Add($"**{index}.** [{title}]({like.TrackUrl})\n└ {author} • {duration}");
            descriptionLines.Add(string.Empty);
        }

        // safety: keep embed description well under 4096
        var description = string.Join("\n", descriptionLines);
        if (description.Length > 3900)
        {
            description = description[..3900] + "\n…";
        }

        embedBuilder.WithDescription(description);

        var components = new ComponentBuilder()
            .WithButton("◀", $"likes_page:{guildId}:{userId}:{page - 1}", ButtonStyle.Secondary, disabled: page <= 1, row: 0)
            .WithButton("▶", $"likes_page:{guildId}:{userId}:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages, row: 0)
            .WithButton("Shuffle", $"likes_shuffle:{guildId}:{userId}", ButtonStyle.Success, emote: new Emoji("🔀"), row: 0)
            .WithButton("Stop", $"likes_stop:{guildId}:{userId}", ButtonStyle.Secondary, emote: new Emoji("⏹"), row: 0);

        for (var i = 0; i < likes.Count; i++)
        {
            var like = likes[i];
            var row = 1 + (i / 5);
            components.WithButton((i + 1).ToString(), $"likes_play:{guildId}:{userId}:{like.Id}", ButtonStyle.Primary, emote: new Emoji("▶"), row: row);
        }

        return (embedBuilder.Build(), components.Build());
    }

    private async Task HandleLikesPageAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !ulong.TryParse(parts[1], out var guildId)
            || !ulong.TryParse(parts[2], out var userId)
            || !int.TryParse(parts[3], out var page))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (component.User.Id != userId)
        {
            await component.RespondAsync("Это не твои лайки.", ephemeral: true);
            return;
        }

        var (embed, components) = await BuildLikesMessageAsync(guildId, userId, page);
        await component.UpdateAsync(msg =>
        {
            msg.Embed = embed;
            msg.Components = components;
        });
    }

    private async Task HandleLikesShuffleAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !ulong.TryParse(parts[1], out var guildId) || !ulong.TryParse(parts[2], out var userId))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (component.User.Id != userId)
        {
            await component.RespondAsync("Это не твои лайки.", ephemeral: true);
            return;
        }

        if (_client.GetGuild(guildId) is not IGuild guild)
        {
            await component.RespondAsync("Сервер не найден.", ephemeral: true);
            return;
        }

        if (component.User is not SocketGuildUser guildUser || guildUser.VoiceChannel == null)
        {
            await component.RespondAsync("Зайди в голосовой канал, чтобы включить лайки.", ephemeral: true);
            return;
        }

        if (component.Channel is not ITextChannel textChannel)
        {
            await component.RespondAsync("Эта кнопка работает только в текстовом канале сервера.", ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);
        var result = await _audioService.StartLikedShuffleAsync(guild, guildUser.VoiceChannel, textChannel, userId);
        await component.FollowupAsync(result.Message, ephemeral: true);
    }

    private async Task HandleLikesStopAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !ulong.TryParse(parts[1], out var guildId) || !ulong.TryParse(parts[2], out var userId))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (component.User.Id != userId)
        {
            await component.RespondAsync("Это не твои лайки.", ephemeral: true);
            return;
        }

        _audioService.DisableLikedShuffle(guildId);
        await component.RespondAsync("Режим лайков выключен.", ephemeral: true);
    }

    private async Task HandleLikesPlayAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !ulong.TryParse(parts[1], out var guildId)
            || !ulong.TryParse(parts[2], out var userId)
            || !long.TryParse(parts[3], out var likeId))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (component.User.Id != userId)
        {
            await component.RespondAsync("Это не твои лайки.", ephemeral: true);
            return;
        }

        if (_client.GetGuild(guildId) is not IGuild guild)
        {
            await component.RespondAsync("Сервер не найден.", ephemeral: true);
            return;
        }

        if (component.User is not SocketGuildUser guildUser || guildUser.VoiceChannel == null)
        {
            await component.RespondAsync("Зайди в голосовой канал, чтобы включить трек из лайков.", ephemeral: true);
            return;
        }

        if (component.Channel is not ITextChannel textChannel)
        {
            await component.RespondAsync("Эта кнопка работает только в текстовом канале сервера.", ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);
        var result = await _audioService.PlayLikedAsync(guild, guildUser.VoiceChannel, textChannel, userId, likeId);
        await component.FollowupAsync(result.Message, ephemeral: true);
    }

    private async Task HandleLikesLikeAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !ulong.TryParse(parts[1], out var guildId))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);
        var result = await _audioService.TryLikeCurrentTrackAsync(guildId, component.User.Id);
        await component.FollowupAsync(result.Message, ephemeral: true);
    }

    private async Task HandleQueueSelectAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !ulong.TryParse(parts[1], out var guildId) || !int.TryParse(parts[2], out var trackIndex))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (!_audioService.TryGetVoiceChannelId(guildId, out var botVoiceChannelId))
        {
            await component.RespondAsync("Бот не подключен к голосовому каналу.", ephemeral: true);
            return;
        }

        if (component.User is not SocketGuildUser guildUser || guildUser.VoiceChannel?.Id != botVoiceChannelId)
        {
            await component.RespondAsync("Зайди в тот же голосовой канал, что и бот, чтобы управлять плеером.", ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);

        var result = await _audioService.TrySelectTrackAsync(guildId, trackIndex);

        if (result.Success)
        {
            await component.FollowupAsync(result.Message, ephemeral: true);
        }
        else
        {
            await component.FollowupAsync(result.Message, ephemeral: true);
        }
    }

    private async Task HandleHistorySelectAsync(SocketMessageComponent component)
    {
        var parts = component.Data.CustomId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !ulong.TryParse(parts[1], out var guildId) || !int.TryParse(parts[2], out var historyIndex))
        {
            await component.RespondAsync("Неверный формат кнопки.", ephemeral: true);
            return;
        }

        if (component.GuildId == null || component.GuildId.Value != guildId)
        {
            await component.RespondAsync("Эта панель не относится к этому серверу.", ephemeral: true);
            return;
        }

        if (!_audioService.TryGetVoiceChannelId(guildId, out var botVoiceChannelId))
        {
            await component.RespondAsync("Бот не подключен к голосовому каналу.", ephemeral: true);
            return;
        }

        if (component.User is not SocketGuildUser guildUser || guildUser.VoiceChannel?.Id != botVoiceChannelId)
        {
            await component.RespondAsync("Зайди в тот же голосовой канал, что и бот, чтобы управлять плеером.", ephemeral: true);
            return;
        }

        if (historyIndex >= 0)
        {
            await component.RespondAsync("Неверный индекс трека.", ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);

        var result = await _audioService.TrySelectHistoryTrackAsync(guildId, stepsBack: -historyIndex);
        await component.FollowupAsync(result.Message, ephemeral: true);
    }

    private async Task UpdatePlayerMessageAsync(ulong guildId)
    {
        if (!_playerMessages.TryGetValue(guildId, out var messageRef))
        {
            return;
        }

        var gate = _updateLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_playerMessages.TryGetValue(guildId, out messageRef))
            {
                return;
            }

            if (_client.GetChannel(messageRef.ChannelId) is not IMessageChannel channel)
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            var existing = await channel.GetMessageAsync(messageRef.MessageId);
            if (existing is not IUserMessage userMessage)
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            var currentTrackHash = GetCurrentTrackHash(guildId);
            _lastTrackHashes.TryGetValue(guildId, out var lastTrackHash);

            var (updatedEmbed, updatedComponents) = await BuildPlayerMessageAsync(guildId);
            await userMessage.ModifyAsync(msg =>
            {
                msg.Embed = updatedEmbed;
                msg.Components = updatedComponents;
            });
            _lastTrackHashes[guildId] = currentTrackHash;

            if (currentTrackHash != null && !string.Equals(currentTrackHash, lastTrackHash, StringComparison.Ordinal))
            {
                RequestTrackChangeBump(guildId);
            }
        }
        catch
        {
            // ignore update failures (e.g. missing permissions/message deleted)
        }
        finally
        {
            gate.Release();
        }
    }

    private void RequestTrackChangeBump(ulong guildId)
    {
        var cts = new CancellationTokenSource();
        _trackChangeBumpTokens.AddOrUpdate(guildId, cts, (_, existing) =>
        {
            existing.Cancel();
            existing.Dispose();
            return cts;
        });

        _ = RunTrackChangeBumpAsync(guildId, cts);
    }

    private void CancelTrackChangeBump(ulong guildId)
    {
        if (_trackChangeBumpTokens.TryRemove(guildId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }

    private async Task RunTrackChangeBumpAsync(ulong guildId, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TrackChangeBumpDelay, cts.Token);
            await BumpPlayerMessageAsync(guildId);
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            // ignore bump failures (e.g. missing permissions/message deleted)
        }
        finally
        {
            if (_trackChangeBumpTokens.TryGetValue(guildId, out var current) && ReferenceEquals(current, cts))
            {
                _trackChangeBumpTokens.TryRemove(guildId, out _);
            }

            cts.Dispose();
        }
    }

    private string? GetCurrentTrackHash(ulong guildId)
    {
        if (!_audioService.TryGetCurrentTrackState(guildId, out var track))
        {
            return null;
        }

        return BuildTrackHash(track);
    }

    private static string BuildTrackHash(TrackState track)
    {
        var source = track.Display.SourceName ?? string.Empty;
        return string.Join('\u001f', track.Display.Title, track.Display.Author, track.Track.Duration.Ticks.ToString(), source);
    }

    private async Task<(Embed embed, MessageComponent components)> BuildPlayerMessageAsync(ulong guildId)
    {
        var player = await _lavaNode.TryGetPlayerAsync(guildId);

        var queueCount = _audioService.GetQueueCount(guildId);
        var historyCount = _audioService.GetHistoryCount(guildId);
        var volume = _audioService.GetVolume(guildId);

        var hasTrack = _audioService.TryGetCurrentTrackState(guildId, out var currentTrack);
        var isPaused = player?.IsPaused ?? false;
        var likedShuffleOn = _audioService.TryGetLikedShuffleUserId(guildId, out _);

        var embed = new EmbedBuilder()
            .WithTitle("Плеер")
            .WithColor(isPaused ? Color.Orange : Color.Green)
            .WithCurrentTimestamp();

        if (player == null)
        {
            embed.WithDescription("Не подключен к голосовому каналу.");
        }
        else if (!hasTrack)
        {
            embed.WithDescription("Сейчас ничего не играет.");
        }
        else
        {
            embed.AddField("Сейчас играет", $"{currentTrack.Display.Title}\n{currentTrack.Display.Author}", inline: false);
        }

        embed.AddField("Статус", player == null ? "Оффлайн" : (isPaused ? "Пауза" : "Играет"), inline: true);
        embed.AddField("Очередь", queueCount.ToString(), inline: true);
        embed.AddField("Громкость", $"{volume}%", inline: true);

        var builder = new ComponentBuilder()
            .WithButton("Назад", BuildCustomId(guildId, "prev"), ButtonStyle.Secondary, emote: new Emoji("⏮️"), disabled: historyCount == 0)
            .WithButton(isPaused ? "Продолжить" : "Пауза", BuildCustomId(guildId, "pause"), ButtonStyle.Primary, emote: new Emoji(isPaused ? "▶️" : "⏸️"), disabled: player == null || !hasTrack)
            .WithButton("Вперёд", BuildCustomId(guildId, "next"), ButtonStyle.Secondary, emote: new Emoji("⏭️"), disabled: queueCount == 0 && !likedShuffleOn)
            .WithButton("❤️", $"likes_like:{guildId}", ButtonStyle.Secondary, disabled: !hasTrack)
            .WithButton("Стоп", BuildCustomId(guildId, "stop"), ButtonStyle.Danger, emote: new Emoji("⏹️"), disabled: player == null || !hasTrack);

        return (embed.Build(), builder.Build());
    }

    public async Task RemovePlayerMessageAsync(ulong guildId)
    {
        if (!_playerMessages.TryGetValue(guildId, out var messageRef))
        {
            _lastTrackHashes.TryRemove(guildId, out _);
            CancelTrackChangeBump(guildId);
            return;
        }

        CancelTrackChangeBump(guildId);
        var gate = _updateLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_playerMessages.TryGetValue(guildId, out messageRef))
            {
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            if (_client.GetChannel(messageRef.ChannelId) is not IMessageChannel channel)
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            try
            {
                var message = await channel.GetMessageAsync(messageRef.MessageId);
                if (message is IUserMessage userMessage)
                {
                    await userMessage.DeleteAsync();
                }
            }
            catch
            {
                // ignore deletion failures (e.g. message already deleted)
            }
            finally
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task BumpPlayerMessageAsync(ulong guildId, bool requireTrack = true)
    {
        if (requireTrack && !_audioService.TryGetCurrentTrackState(guildId, out _))
        {
            return;
        }

        if (!_playerMessages.TryGetValue(guildId, out var messageRef))
        {
            return;
        }

        var gate = _updateLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (requireTrack && !_audioService.TryGetCurrentTrackState(guildId, out _))
            {
                return;
            }

            if (!_playerMessages.TryGetValue(guildId, out messageRef))
            {
                return;
            }

            if (_client.GetChannel(messageRef.ChannelId) is not IMessageChannel channel)
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            var existing = await channel.GetMessageAsync(messageRef.MessageId);
            if (existing is not IUserMessage oldMessage)
            {
                _playerMessages.TryRemove(guildId, out _);
                _lastTrackHashes.TryRemove(guildId, out _);
                return;
            }

            var (embed, components) = await BuildPlayerMessageAsync(guildId);
            var sent = await channel.SendMessageAsync(embed: embed, components: components);
            _playerMessages[guildId] = new PlayerMessageRef(channel.Id, sent.Id);
            _lastTrackHashes[guildId] = GetCurrentTrackHash(guildId);

            try
            {
                await oldMessage.DeleteAsync();
            }
            catch
            {
                // ignore deletion failures
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string BuildCustomId(ulong guildId, string action) => $"{CustomIdPrefix}:{guildId}:{action}";

    private static bool TryParseCustomId(string customId, out ulong guildId, out string action)
    {
        guildId = default;
        action = string.Empty;

        if (string.IsNullOrWhiteSpace(customId))
        {
            return false;
        }

        var parts = customId.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], CustomIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ulong.TryParse(parts[1], out guildId))
        {
            return false;
        }

        action = parts[2].ToLowerInvariant();
        return true;
    }

    private sealed record PlayerMessageRef(ulong ChannelId, ulong MessageId);
}
