namespace DiscordMusicBot;

public sealed class GuildSettingsEntity
{
    public long GuildId { get; set; }
    public StreamingSource PreferredSource { get; set; } = StreamingSource.Auto;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

