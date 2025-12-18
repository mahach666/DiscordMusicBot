namespace DiscordMusicBot;

public sealed class TrackLikeEntity
{
    public long Id { get; set; }
    public long GuildId { get; set; }
    public long UserId { get; set; }

    public string TrackUrl { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Author { get; set; } = null!;
    public string? SourceName { get; set; }

    public long DurationMs { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

