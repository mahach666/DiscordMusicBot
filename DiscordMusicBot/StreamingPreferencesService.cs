using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace DiscordMusicBot;

public sealed class StreamingPreferencesService
{
    private readonly ConcurrentDictionary<ulong, StreamingSource> _preferredSources = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;

    public StreamingPreferencesService(IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
    }

    public async Task<StreamingSource> GetPreferredSourceAsync(ulong guildId)
    {
        if (_preferredSources.TryGetValue(guildId, out var cached))
        {
            return cached;
        }

        if (_dbFactory == null)
        {
            return StreamingSource.Auto;
        }

        var gate = _guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_preferredSources.TryGetValue(guildId, out cached))
            {
                return cached;
            }

            await using var db = _dbFactory.CreateDbContext();
            var row = await db.GuildSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.GuildId == Snowflake.ToLong(guildId));

            var source = row?.PreferredSource ?? StreamingSource.Auto;
            _preferredSources[guildId] = source;
            return source;
        }
        catch
        {
            return StreamingSource.Auto;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetPreferredSourceAsync(ulong guildId, StreamingSource source)
    {
        _preferredSources[guildId] = source;

        if (_dbFactory == null)
        {
            return;
        }

        try
        {
            await using var db = _dbFactory.CreateDbContext();
            var id = Snowflake.ToLong(guildId);
            var row = await db.GuildSettings.SingleOrDefaultAsync(x => x.GuildId == id);
            if (row == null)
            {
                row = new GuildSettingsEntity { GuildId = id };
                db.GuildSettings.Add(row);
            }

            row.PreferredSource = source;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch
        {
            // ignore persistence failures
        }
    }
}
