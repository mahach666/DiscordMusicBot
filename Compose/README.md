# Docker / Compose

В папке `Compose/` лежит всё для:

- локального запуска бота в Docker;
- сборки образа;
- упаковки образа в `.tar`;
- публикации образа в реестр (например, GHCR).

## Локальный запуск (настройки из `.env`)

1) Создай файл `Compose/.env` на основе примера:

```powershell
Copy-Item Compose/env.example Compose/.env
```

2) Заполни `DISCORD_TOKEN` и `YOUTUBE_API_KEY` в `Compose/.env`.

3) (Опционально) Добавь `YANDEX_MUSIC_TOKEN`, чтобы включить Yandex Music.

4) (Опционально, но нужно для лайков и сохранения настроек сервера) — укажи Postgres:

- одной строкой: `DATABASE_URL` **или**
- параметрами: `POSTGRES_HOST/PORT/DB/USER/PASSWORD` (удобно для docker-compose)

Примечания:

- YouTube-плагин Lavalink скачивается в образ на этапе сборки (в `/app/plugins`).
- Если трек находится, но звука нет на Linux — проверь наличие `libopus0` и `libsodium23` (в `Compose/Dockerfile` они уже установлены).

5) Запусти:

```powershell
powershell -File Compose/tasks.ps1 up
```

Остановить:

```powershell
powershell -File Compose/tasks.ps1 down
```

Логи:

```powershell
powershell -File Compose/tasks.ps1 logs
```

## Сборка образа

```powershell
powershell -File Compose/tasks.ps1 build
```

По умолчанию: `discordmusicbot:local`. Можно переопределить:

```powershell
powershell -File Compose/tasks.ps1 build -ImageName discordmusicbot -ImageTag v1
```

## Упаковка образа в tar

```powershell
powershell -File Compose/tasks.ps1 save
```

Файл появится в `Compose/dist/`. Загрузить обратно:

```powershell
powershell -File Compose/tasks.ps1 load -TarPath .\Compose\dist\discordmusicbot_local.tar
```

## Публикация образа (remote/CI: настройки из параметров или env)

Пример для GHCR (нужен `docker login`):

```powershell
powershell -File Compose/tasks.ps1 push -ImageRegistry ghcr.io/<owner>/ -ImageName discordmusicbot -ImageTag latest
```

Если нужно логиниться параметрами:

```powershell
powershell -File Compose/tasks.ps1 push -ImageRegistry ghcr.io/<owner>/ -RegistryUsername <user> -RegistryPassword <token>
```

## GitHub Actions (автопубликация)

Workflow: `.github/workflows/docker-publish.yml` (собирает `Compose/Dockerfile` и пушит в `ghcr.io`).

