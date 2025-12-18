namespace DiscordMusicBot;

public static class Snowflake
{
    public static long ToLong(ulong value) => checked((long)value);
    public static ulong ToUlong(long value) => checked((ulong)value);
}

