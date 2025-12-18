using Microsoft.EntityFrameworkCore;

namespace DiscordMusicBot;

public sealed class AppDbContext : DbContext
{
    public DbSet<GuildSettingsEntity> GuildSettings => Set<GuildSettingsEntity>();
    public DbSet<TrackLikeEntity> TrackLikes => Set<TrackLikeEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuildSettingsEntity>(b =>
        {
            b.ToTable("guild_settings");
            b.HasKey(x => x.GuildId);
            b.Property(x => x.GuildId).ValueGeneratedNever();
            b.Property(x => x.PreferredSource).HasConversion<int>();
            b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        });

        modelBuilder.Entity<TrackLikeEntity>(b =>
        {
            b.ToTable("track_likes");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.GuildId, x.UserId, x.TrackUrl }).IsUnique();

            b.Property(x => x.TrackUrl).HasMaxLength(2048);
            b.Property(x => x.Title).HasMaxLength(512);
            b.Property(x => x.Author).HasMaxLength(512);
            b.Property(x => x.SourceName).HasMaxLength(64);
            b.Property(x => x.AddedAt).HasColumnType("timestamptz");
        });
    }
}

