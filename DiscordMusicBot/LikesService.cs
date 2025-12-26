using Microsoft.EntityFrameworkCore;

namespace DiscordMusicBot;

public sealed record TrackLikeDto(
    long Id,
    string TrackUrl,
    string Title,
    string Author,
    string? SourceName,
    TimeSpan Duration,
    DateTimeOffset AddedAt);

public sealed class LikesService
{
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;

    public LikesService(IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
    }

    public bool IsEnabled => _dbFactory != null;

    public async Task<int> GetLikesCountAsync(ulong guildId, ulong userId)
    {
        if (_dbFactory == null)
        {
            return 0;
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        return await db.TrackLikes.AsNoTracking()
            .Where(x => x.GuildId == gid && x.UserId == uid)
            .CountAsync();
    }

    public async Task<(bool Success, string Message)> LikeAsync(ulong guildId, ulong userId, TrackState track)
    {
        if (_dbFactory == null)
        {
            return (false, "База данных не настроена. Запусти Postgres и укажи переменные окружения для БД.");
        }

        var url = !string.IsNullOrWhiteSpace(track.Display.Url) ? track.Display.Url : (track.Track.Url ?? string.Empty);
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "Не удалось поставить лайк: у трека нет URL.");
        }

        await using var db = _dbFactory.CreateDbContext();

        var entity = new TrackLikeEntity
        {
            GuildId = Snowflake.ToLong(guildId),
            UserId = Snowflake.ToLong(userId),
            TrackUrl = url,
            Title = track.Display.Title ?? url,
            Author = track.Display.Author ?? string.Empty,
            SourceName = track.Display.SourceName,
            DurationMs = (long)track.Track.Duration.TotalMilliseconds,
            AddedAt = DateTimeOffset.UtcNow
        };

        var exists = await db.TrackLikes.AnyAsync(x =>
            x.GuildId == entity.GuildId && x.UserId == entity.UserId && x.TrackUrl == entity.TrackUrl);
        if (exists)
        {
            return (true, "Уже есть в лайках.");
        }

        db.TrackLikes.Add(entity);
        await db.SaveChangesAsync();
        return (true, $"Добавлено в лайки: **{entity.Title}**");
    }

    public async Task<(bool Success, string Message)> UnlikeAsync(ulong guildId, ulong userId, TrackState track)
    {
        if (_dbFactory == null)
        {
            return (false, "База данных не настроена. Запусти Postgres и укажи переменные окружения для БД.");
        }

        var url = !string.IsNullOrWhiteSpace(track.Display.Url) ? track.Display.Url : (track.Track.Url ?? string.Empty);
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "Не удалось убрать лайк: у трека нет URL.");
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        var removed = await db.TrackLikes
            .Where(x => x.GuildId == gid && x.UserId == uid && x.TrackUrl == url)
            .ExecuteDeleteAsync();

        return removed > 0 ? (true, "Удалено из лайков.") : (true, "Этого трека нет в лайках.");
    }

    public async Task<IReadOnlyList<TrackLikeDto>> GetLikesAsync(ulong guildId, ulong userId, int limit = 10, int offset = 0)
    {
        if (_dbFactory == null)
        {
            return Array.Empty<TrackLikeDto>();
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        var items = await db.TrackLikes.AsNoTracking()
            .Where(x => x.GuildId == gid && x.UserId == uid)
            .OrderByDescending(x => x.AddedAt)
            .Skip(offset)
            .Take(limit)
            .Select(x => new TrackLikeDto(
                x.Id,
                x.TrackUrl,
                x.Title,
                x.Author,
                x.SourceName,
                TimeSpan.FromMilliseconds(x.DurationMs),
                x.AddedAt))
            .ToListAsync();

        return items;
    }

    public async Task<TrackLikeDto?> GetLikeByIndexAsync(ulong guildId, ulong userId, int index)
    {
        if (_dbFactory == null)
        {
            return null;
        }

        if (index <= 0)
        {
            return null;
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        return await db.TrackLikes.AsNoTracking()
            .Where(x => x.GuildId == gid && x.UserId == uid)
            .OrderByDescending(x => x.AddedAt)
            .Skip(index - 1)
            .Take(1)
            .Select(x => new TrackLikeDto(
                x.Id,
                x.TrackUrl,
                x.Title,
                x.Author,
                x.SourceName,
                TimeSpan.FromMilliseconds(x.DurationMs),
                x.AddedAt))
            .SingleOrDefaultAsync();
    }

    public async Task<TrackLikeDto?> GetLikeAsync(ulong guildId, ulong userId, long likeId)
    {
        if (_dbFactory == null)
        {
            return null;
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        var item = await db.TrackLikes.AsNoTracking()
            .Where(x => x.Id == likeId && x.GuildId == gid && x.UserId == uid)
            .Select(x => new TrackLikeDto(
                x.Id,
                x.TrackUrl,
                x.Title,
                x.Author,
                x.SourceName,
                TimeSpan.FromMilliseconds(x.DurationMs),
                x.AddedAt))
            .SingleOrDefaultAsync();

        return item;
    }

    public async Task<TrackLikeDto?> GetRandomLikeAsync(ulong guildId, ulong userId, IReadOnlyCollection<string>? excludeUrls = null)
    {
        if (_dbFactory == null)
        {
            return null;
        }

        await using var db = _dbFactory.CreateDbContext();
        var gid = Snowflake.ToLong(guildId);
        var uid = Snowflake.ToLong(userId);

        var query = db.TrackLikes.AsNoTracking()
            .Where(x => x.GuildId == gid && x.UserId == uid);

        if (excludeUrls is { Count: > 0 })
        {
            query = query.Where(x => !excludeUrls.Contains(x.TrackUrl));
        }

        var count = await query.CountAsync();
        if (count == 0 && excludeUrls is { Count: > 0 })
        {
            // если все исключили — пробуем без исключений
            return await GetRandomLikeAsync(guildId, userId);
        }

        if (count == 0)
        {
            return null;
        }

        var offset = Random.Shared.Next(count);

        var item = await query
            .OrderBy(x => x.Id)
            .Skip(offset)
            .Take(1)
            .Select(x => new TrackLikeDto(
                x.Id,
                x.TrackUrl,
                x.Title,
                x.Author,
                x.SourceName,
                TimeSpan.FromMilliseconds(x.DurationMs),
                x.AddedAt))
            .SingleAsync();

        return item;
    }
}
