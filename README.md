# DiscordMusicBot

Музыкальный бот для Discord на C# (.NET) с воспроизведением через Lavalink (Victoria) и текстовыми командами (`!` или упоминание бота).

## Возможности

- Воспроизведение по URL или поисковому запросу через Lavalink (`ytsearch:`, `ytmsearch:`, `scsearch:`).
- Поддержка источников (как «приоритет» для поиска): YouTube, YouTube Music, SoundCloud, Yandex Music (опционально).
- Очередь, история, пауза/продолжить, пропуск, громкость.
- Панель плеера с кнопками (`!player`).
- Лайки треков и shuffle лайков (требует Postgres).

## Требования

- .NET SDK: требуется версия, поддерживающая `net10.0` (см. `DiscordMusicBot/DiscordMusicBot.csproj`).
- Java 17+ (для Lavalink).
- Lavalink v4 (можно автозапуском или отдельным процессом).
- (Опционально) Postgres 16+ для лайков и сохранения настроек сервера.

## Быстрый старт (локально)

1) Создай бота в Discord Developer Portal и включи intent **Message Content** (бот читает текстовые команды).

2) Задай конфиг через переменные окружения (рекомендуется) или `.env` (опциональный fallback для локального запуска).

Важно: `.env` — это не «параметры запуска». Параметры/флаги передаются обычными аргументами командной строки, а файл нужен только как удобный способ хранить секреты в формате `KEY=value`, если не хочется настраивать переменные окружения в системе.

Пример через переменные окружения (PowerShell, только на текущую сессию):

```powershell
$env:DISCORD_TOKEN="..."
$env:YOUTUBE_API_KEY="..."
# опционально
$env:YANDEX_MUSIC_TOKEN="..."
$env:DATABASE_URL="Host=localhost;Port=5432;Database=discordmusicbot;Username=discordmusicbot;Password=discordmusicbot;SslMode=Disable"
```

`.env` бот ищет в нескольких местах; для `dotnet run` проще всего создать файл `DiscordMusicBot/.env` (а для опубликованного `DiscordMusicBot.exe` — положить `.env` рядом с exe):

```env
DISCORD_TOKEN=...
YOUTUBE_API_KEY=...

# опционально (Yandex Music)
YANDEX_MUSIC_TOKEN=...

# опционально (Postgres: лайки и сохранение настроек сервера)
# DATABASE_URL=Host=localhost;Port=5432;Database=discordmusicbot;Username=discordmusicbot;Password=discordmusicbot;SslMode=Disable
```

3) Запусти (аргументы после `--`):

```powershell
dotnet run --project .\DiscordMusicBot\DiscordMusicBot.csproj -- [--no-lavalink] [--test-yandex]
```

По умолчанию бот пытается сам поднять `Lavalink.jar` (если найден рядом с приложением) и подключается к `127.0.0.1:2333` с паролем `youshallnotpass`.

## Запуск Lavalink отдельно

Если Lavalink запускается отдельно — он должен совпадать по настройкам с кодом бота и `DiscordMusicBot/application.yml`:

```powershell
java -jar .\DiscordMusicBot\Lavalink.jar
dotnet run --project .\DiscordMusicBot\DiscordMusicBot.csproj -- --no-lavalink
```

## Docker / Compose

Для запуска в Docker см. `Compose/README.md`.

## Команды

Префикс: `!` (также работает упоминание бота).

- `!join` / `!j` — подключиться к голосовому каналу.
- `!leave` / `!l` — отключиться.
- `!play <запрос|URL>` / `!p` — добавить трек/плейлист в очередь или запустить.
- `!source [auto|youtube|ytmusic|soundcloud|yandexmusic]` — показать/установить приоритет источника поиска (сохраняется в Postgres, если он настроен).
- `!queue` / `!q` — очередь (с кнопками выбора).
- `!skip` / `!next` / `!n` — пропуск.
- `!pause` / `!resume` — пауза/продолжить.
- `!stop` — остановить и очистить очередь.
- `!volume <0-100>` / `!vol` — громкость.
- `!player` / `!controls` / `!ui` — панель управления с кнопками.
- `!status` / `!stat` — статус бота и Lavalink.
- `!help` / `!h` — справка.
- `!search <запрос>` / `!s` — поиск в YouTube через YouTube Data API (информационная выдача).
- `!like` / `!fav`, `!unlike` / `!unfav`, `!likes` / `!favs` — лайки (требует Postgres), `!likes shuffle`, `!likes stop`.

## Благодарности

- Lavalink & плагины: https://github.com/lavalink-devs
- Документация по Yandex Music: https://yandex-music.readthedocs.io/en/main/index.html
- Yandex.Music.Api: https://github.com/K1llMan/Yandex.Music.Api

## Лицензия

Код проекта распространяется по лицензии MIT — см. `LICENSE`. Сторонние зависимости и бинарники (например, `Lavalink.jar`) имеют свои лицензии.

