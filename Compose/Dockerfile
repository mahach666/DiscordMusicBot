FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DiscordMusicBot.sln ./
COPY DiscordMusicBot/DiscordMusicBot.csproj DiscordMusicBot/
RUN dotnet restore DiscordMusicBot/DiscordMusicBot.csproj

COPY DiscordMusicBot/ DiscordMusicBot/
RUN dotnet publish DiscordMusicBot/DiscordMusicBot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
  && apt-get install -y --no-install-recommends openjdk-17-jre-headless ca-certificates curl libopus0 libsodium23 \
  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/ ./
COPY DiscordMusicBot/Lavalink.jar ./Lavalink.jar
COPY DiscordMusicBot/application.yml ./application.yml

ARG YOUTUBE_PLUGIN_VERSION=1.16.0
RUN mkdir -p /app/plugins \
  && curl -fsSL -o "/app/plugins/youtube-plugin-${YOUTUBE_PLUGIN_VERSION}.jar" "https://maven.lavalink.dev/releases/dev/lavalink/youtube/youtube-plugin/${YOUTUBE_PLUGIN_VERSION}/youtube-plugin-${YOUTUBE_PLUGIN_VERSION}.jar" \
  && chown -R app:app /app

USER app
ENTRYPOINT ["dotnet", "DiscordMusicBot.dll"]
