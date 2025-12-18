using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.Sockets;
using Victoria;

namespace DiscordMusicBot;

public class Program
{
    private static IServiceProvider _services = null!;
    private static DiscordSocketClient _client = null!;
    private static CommandService _commands = null!;
    private static LavaNode _lavaNode = null!;
    private static Process? _lavaLinkProcess;
    private static readonly TaskCompletionSource<bool> _discordReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string LavalinkHost = "127.0.0.1";
    private const int LavalinkPort = 2333;
    private const string LavalinkPassword = "youshallnotpass";
    private const int LavalinkApiVersion = 4;

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            Console.WriteLine("Запуск Discord Music Bot...");

            bool autoStartLavalink = !args.Contains("--no-lavalink");

            var config = LoadConfiguration();

            if (autoStartLavalink)
            {
                Console.WriteLine("Запуск Lavalink сервера...");
                await StartLavalinkAsync();
            }
            else
            {
                Console.WriteLine("Пропуск автоматического запуска Lavalink (--no-lavalink)");
            }

            _services = ConfigureServices(config);

            _client = _services.GetRequiredService<DiscordSocketClient>();
            _commands = _services.GetRequiredService<CommandService>();
            _lavaNode = _services.GetRequiredService<LavaNode>();

            var databaseBootstrapper = _services.GetRequiredService<DatabaseBootstrapper>();
            await databaseBootstrapper.InitializeAsync();

            // Optional: test Yandex Music API on start (adds an extra request on startup)
            if (args.Contains("--test-yandex", StringComparer.OrdinalIgnoreCase))
            {
                var yandexService = _services.GetRequiredService<YandexMusicService>();
                if (yandexService.IsEnabled)
                {
                    Console.WriteLine("Testing Yandex Music API...");
                    var testTracks = await yandexService.SearchTracksAsync("summertime sadness", 1);
                    Console.WriteLine($"Yandex Music test result: {testTracks.Count} tracks found");
                    if (testTracks.Count > 0)
                    {
                        Console.WriteLine($"First track: {yandexService.GetTrackTitle(testTracks[0])}");
                    }
                }
            }

            // Связываем сервисы
            var audioService = _services.GetRequiredService<AudioService>();
            var playerUiService = _services.GetRequiredService<PlayerUiService>();
            audioService.SetPlayerUiService(playerUiService);

            await ConfigureAsync();

            Console.WriteLine("Подключение к Discord...");
            await _client.LoginAsync(TokenType.Bot, config.DiscordToken);
            await _client.StartAsync();

            await WaitForDiscordReadyAsync();

            Console.WriteLine("Проверка подключения Lavalink...");
            await WaitForLavalinkReadyAsync();

            Console.WriteLine("Бот запущен! Нажмите Ctrl+C для выхода.");

            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitEvent.Set();
            };

            exitEvent.WaitOne();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка запуска: {ex.Message}");
        }
        finally
        {
            StopLavalink();
        }
    }

    private static IServiceProvider ConfigureServices(Config config)
    {
        var services = new ServiceCollection()
            .AddSingleton(config)
            .AddLogging()
            .AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = LogSeverity.Info,
                MessageCacheSize = 100,
                GatewayIntents = GatewayIntents.Guilds
                                | GatewayIntents.GuildMessages
                                | GatewayIntents.MessageContent
                                | GatewayIntents.GuildVoiceStates
            }))
            .AddSingleton(new CommandService(new CommandServiceConfig
            {
                LogLevel = LogSeverity.Info,
                CaseSensitiveCommands = false
            }))
            .AddLavaNode<LavaNode, LavaPlayer<LavaTrack>, LavaTrack>(x =>
            {
                x.Version = LavalinkApiVersion;
                x.SelfDeaf = false;
                x.Hostname = LavalinkHost;
                x.Port = LavalinkPort;
                x.Authorization = LavalinkPassword;
            })
            .AddSingleton<DatabaseBootstrapper>();

        if (!string.IsNullOrWhiteSpace(config.DatabaseConnectionString))
        {
            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseNpgsql(config.DatabaseConnectionString, npgsql =>
                    npgsql.EnableRetryOnFailure(3)));
        }

        return services
            .AddSingleton(new YandexMusicService(config.YandexMusicToken))
            .AddSingleton<StreamingPreferencesService>()
            .AddSingleton<LikesService>()
            .AddSingleton<AudioService>()
            .AddSingleton<PlayerUiService>()
            .AddSingleton<MusicCommands>()
            .BuildServiceProvider();
    }

    private static async Task ConfigureAsync()
    {
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;

        var playerUiService = _services.GetRequiredService<PlayerUiService>();
        _client.ButtonExecuted += playerUiService.HandleButtonAsync;

        await _commands.AddModuleAsync<MusicCommands>(_services);

        _client.MessageReceived += HandleCommandAsync;
    }

    private static async Task HandleCommandAsync(SocketMessage arg)
    {
        if (arg is not SocketUserMessage msg)
        {
            return;
        }

        if (msg.Author.IsBot)
        {
            return;
        }

        int pos = 0;
        if (!msg.HasCharPrefix('!', ref pos) && !msg.HasMentionPrefix(_client.CurrentUser, ref pos))
        {
            return;
        }

        var context = new SocketCommandContext(_client, msg);
        var result = await _commands.ExecuteAsync(context, pos, _services);
        if (!result.IsSuccess)
        {
            await msg.Channel.SendMessageAsync($"Ошибка: {result.ErrorReason}");
        }
    }

    private static Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log.ToString());
        return Task.CompletedTask;
    }

    private static Task ReadyAsync()
    {
        _discordReadyTcs.TrySetResult(true);
        Console.WriteLine($"Бот {_client.CurrentUser.Username} готов!");
        Console.WriteLine($"Зарегистрировано команд: {_commands.Commands.Count()}");
        return Task.CompletedTask;
    }

    private static async Task WaitForDiscordReadyAsync()
    {
        var completed = await Task.WhenAny(_discordReadyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed != _discordReadyTcs.Task)
        {
            Console.WriteLine("Не дождались события Discord Ready за 30 секунд, продолжаем запуск.");
        }
    }

    private static Config LoadConfiguration()
    {
        var discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        var youtubeApiKey = Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
        var yandexMusicToken = Environment.GetEnvironmentVariable("YANDEX_MUSIC_TOKEN");

        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        var postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");
        var postgresPort = Environment.GetEnvironmentVariable("POSTGRES_PORT");
        var postgresDb = Environment.GetEnvironmentVariable("POSTGRES_DB");
        var postgresUser = Environment.GetEnvironmentVariable("POSTGRES_USER");
        var postgresPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        var postgresSslMode = Environment.GetEnvironmentVariable("POSTGRES_SSLMODE");

        // Читаем .env даже если токены пришли из ENV: там могут быть настройки БД для локального запуска.
        {
            var possiblePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, ".env"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env"),
                Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                ".env",
            };

            foreach (var envPath in possiblePaths)
            {
                if (!File.Exists(envPath))
                {
                    continue;
                }

                Console.WriteLine($"Найден файл конфигурации: {envPath}");
                foreach (var line in File.ReadAllLines(envPath))
                {
                    var trimmedLine = line.Trim();
                    if (trimmedLine.StartsWith("DISCORD_TOKEN=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(discordToken))
                        {
                            discordToken = trimmedLine["DISCORD_TOKEN=".Length..].Trim();
                        }
                    }
                    else if (trimmedLine.StartsWith("YOUTUBE_API_KEY=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(youtubeApiKey))
                        {
                            youtubeApiKey = trimmedLine["YOUTUBE_API_KEY=".Length..].Trim();
                        }
                    }
                    else if (trimmedLine.StartsWith("YANDEX_MUSIC_TOKEN=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(yandexMusicToken))
                        {
                            yandexMusicToken = trimmedLine["YANDEX_MUSIC_TOKEN=".Length..].Trim();
                        }
                    }
                    else if (trimmedLine.StartsWith("DATABASE_URL=", StringComparison.OrdinalIgnoreCase))
                    {
                        databaseUrl ??= trimmedLine["DATABASE_URL=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("DB_CONNECTION_STRING=", StringComparison.OrdinalIgnoreCase))
                    {
                        databaseUrl ??= trimmedLine["DB_CONNECTION_STRING=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_HOST=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresHost ??= trimmedLine["POSTGRES_HOST=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_PORT=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresPort ??= trimmedLine["POSTGRES_PORT=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_DB=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresDb ??= trimmedLine["POSTGRES_DB=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_USER=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresUser ??= trimmedLine["POSTGRES_USER=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_PASSWORD=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresPassword ??= trimmedLine["POSTGRES_PASSWORD=".Length..].Trim();
                    }
                    else if (trimmedLine.StartsWith("POSTGRES_SSLMODE=", StringComparison.OrdinalIgnoreCase))
                    {
                        postgresSslMode ??= trimmedLine["POSTGRES_SSLMODE=".Length..].Trim();
                    }
                }

                break;
            }
        }

        Console.WriteLine($"Discord Token длина: {discordToken?.Length ?? 0}");
        Console.WriteLine($"YouTube API Key длина: {youtubeApiKey?.Length ?? 0}");
        Console.WriteLine($"Yandex Music Token длина: {yandexMusicToken?.Length ?? 0}");

        if (string.IsNullOrWhiteSpace(discordToken))
        {
            throw new Exception("DISCORD_TOKEN не найден. Установите переменную окружения DISCORD_TOKEN или создайте файл .env с этой переменной");
        }

        if (string.IsNullOrWhiteSpace(youtubeApiKey))
        {
            throw new Exception("YOUTUBE_API_KEY не найден. Установите переменную окружения YOUTUBE_API_KEY или создайте файл .env с этой переменной");
        }

        return new Config
        {
            DiscordToken = discordToken,
            YouTubeApiKey = youtubeApiKey,
            DatabaseConnectionString = BuildDatabaseConnectionString(databaseUrl, postgresHost, postgresPort, postgresDb, postgresUser, postgresPassword, postgresSslMode),
            YandexMusicToken = yandexMusicToken
        };
    }

    private static string? BuildDatabaseConnectionString(
        string? databaseUrl,
        string? host,
        string? port,
        string? db,
        string? user,
        string? password,
        string? sslMode)
    {
        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return databaseUrl.Trim();
        }

        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(db)
            || string.IsNullOrWhiteSpace(user)
            || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var normalizedPort = 5432;
        if (!string.IsNullOrWhiteSpace(port) && int.TryParse(port, out var parsedPort))
        {
            normalizedPort = parsedPort;
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = host.Trim(),
            Port = normalizedPort,
            Database = db.Trim(),
            Username = user.Trim(),
            Password = password.Trim(),
            Pooling = true,
            Timeout = 5,
            CommandTimeout = 30,
            MaxPoolSize = 20
        };

        if (!string.IsNullOrWhiteSpace(sslMode) && Enum.TryParse<Npgsql.SslMode>(sslMode, ignoreCase: true, out var parsedSsl))
        {
            builder.SslMode = parsedSsl;
        }

        return builder.ConnectionString;
    }

    private static async Task StartLavalinkAsync()
    {
        try
        {
            if (IsLavalinkAlreadyRunning())
            {
                Console.WriteLine("Lavalink уже запущен, пропускаем запуск");
                return;
            }

            var lavalinkJarPath = Path.Combine(AppContext.BaseDirectory, "Lavalink.jar");
            var altPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Lavalink.jar");

            string? jarPath = null;
            if (File.Exists(lavalinkJarPath))
            {
                jarPath = lavalinkJarPath;
            }
            else if (File.Exists(altPath))
            {
                jarPath = altPath;
            }

            if (jarPath == null)
            {
                throw new FileNotFoundException("Lavalink.jar не найден. Скачайте Lavalink.jar и поместите в папку с приложением.");
            }

            var (javaInstalled, javaVersion) = CheckJavaVersion();
            if (!javaInstalled)
            {
                throw new Exception("Java не установлен. Скачайте и установите Java 17 или выше с https://adoptium.net/");
            }

            if (javaVersion != null && int.TryParse(javaVersion, out int version) && version < 17)
            {
                throw new Exception($"У вас установлена Java {version}, но Lavalink требует Java 17 или выше. Обновите Java: https://adoptium.net/");
            }

            _lavaLinkProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "java",
                    Arguments = $"-jar \"{jarPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(jarPath),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            _lavaLinkProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"[Lavalink] {e.Data}");
                }
            };

            _lavaLinkProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"[Lavalink ERROR] {e.Data}");
                }
            };

            Console.WriteLine($"Запуск Lavalink из: {jarPath}");
            _lavaLinkProcess.Start();
            _lavaLinkProcess.BeginOutputReadLine();
            _lavaLinkProcess.BeginErrorReadLine();

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка запуска Lavalink: {ex.Message}");
            throw;
        }
    }

    private static void StopLavalink()
    {
        if (_lavaLinkProcess is null)
        {
            return;
        }

        try
        {
            if (!_lavaLinkProcess.HasExited)
            {
                _lavaLinkProcess.Kill();
                _lavaLinkProcess.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка остановки Lavalink: {ex.Message}");
        }
    }

    private static (bool installed, string? version) CheckJavaVersion()
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "java",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return (false, null);
            }

            string versionOutput = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return (false, null);
            }

            var versionMatch = System.Text.RegularExpressions.Regex.Match(versionOutput, @"version ""(\d+)""");
            if (versionMatch.Success)
            {
                return (true, versionMatch.Groups[1].Value);
            }

            return (true, "неизвестная версия");
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool IsLavalinkAlreadyRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!process.ProcessName.Contains("java", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var commandLine = GetCommandLine(process);
                    if (!string.IsNullOrEmpty(commandLine) && commandLine.Contains("Lavalink.jar", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Найден запущенный процесс Lavalink: PID {process.Id}");
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(LavalinkHost, LavalinkPort, null, null);
                var success = result.AsyncWaitHandle.WaitOne(1000);
                if (success)
                {
                    client.EndConnect(result);
                    Console.WriteLine($"Порт {LavalinkPort} уже занят, Lavalink возможно уже запущен");
                    return true;
                }
            }
            catch
            {
                // port free
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при проверке запущенного Lavalink: {ex.Message}");
            return false;
        }
    }

    private static async Task WaitForLavalinkReadyAsync()
    {
        int attempts = 0;
        const int maxAttempts = 15;

        while (attempts < maxAttempts && !_lavaNode.IsConnected)
        {
            attempts++;

            try
            {
                await _lavaNode.ConnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка подключения к Lavalink (попытка {attempts}/{maxAttempts}): {ex.Message}");
            }

            bool lavalinkResponds = await TestLavalinkConnection();
            Console.WriteLine(lavalinkResponds
                ? $"Lavalink отвечает на HTTP, но WebSocket не подключен (попытка {attempts}/{maxAttempts})"
                : $"Lavalink не отвечает на HTTP (попытка {attempts}/{maxAttempts})");

            if (attempts % 5 == 0)
            {
                Console.WriteLine($"Все еще ждем подключения Lavalink... ({attempts * 3} сек)");
            }

            if (!_lavaNode.IsConnected)
            {
                await Task.Delay(3000);
            }
        }

        if (_lavaNode.IsConnected)
        {
            Console.WriteLine("Lavalink подключен и готов к работе");
        }
        else
        {
            Console.WriteLine("Lavalink не подключился через WebSocket, но продолжаем запуск бота");
        }
    }

    private static async Task<bool> TestLavalinkConnection()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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

    private static string GetCommandLine(Process process)
    {
        try
        {
            var query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}";
            var searcher = new System.Management.ManagementObjectSearcher(query);
            var results = searcher.Get();

            foreach (var result in results)
            {
                return result["CommandLine"]?.ToString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }
}

public class Config
{
    public string DiscordToken { get; set; } = null!;
    public string YouTubeApiKey { get; set; } = null!;
    public string? DatabaseConnectionString { get; set; }
    public string? YandexMusicToken { get; set; }
}
