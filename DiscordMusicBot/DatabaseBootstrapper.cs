using Microsoft.EntityFrameworkCore;

namespace DiscordMusicBot;

public sealed class DatabaseBootstrapper
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

    private readonly IDbContextFactory<AppDbContext>? _dbFactory;

    public DatabaseBootstrapper(IDbContextFactory<AppDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
    }

    public bool IsEnabled => _dbFactory != null;

    public async Task InitializeAsync()
    {
        if (_dbFactory == null)
        {
            Console.WriteLine("Postgres не настроен: настройки/лайки будут храниться только в памяти.");
            return;
        }

        var deadline = DateTimeOffset.UtcNow.Add(StartupTimeout);
        var delay = InitialDelay;

        while (true)
        {
            try
            {
                await using var db = _dbFactory.CreateDbContext();
                await db.Database.EnsureCreatedAsync();
                Console.WriteLine("Postgres подключен: схема БД готова.");
                return;
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                Console.WriteLine($"Postgres недоступен, повтор через {delay.TotalSeconds:0}s: {ex.Message}");
                await Task.Delay(delay);
                var nextSeconds = Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds);
                delay = TimeSpan.FromSeconds(nextSeconds);
            }
        }
    }
}

