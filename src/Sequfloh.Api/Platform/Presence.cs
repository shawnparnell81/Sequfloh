using System.Collections.Concurrent;

namespace Sequfloh.Platform;

/// <summary>Who is signed in and active. Online means authenticated activity within the last couple of minutes. Writes are skipped when a recent one is already stored.</summary>
public static class Presence
{
    public static readonly TimeSpan OnlineFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TouchEvery = TimeSpan.FromSeconds(45);

    public static bool IsOnline(DateTimeOffset? seen, DateTimeOffset? now = null) =>
        seen is { } at && (now ?? DateTimeOffset.UtcNow) - at <= OnlineFor;

    static readonly ConcurrentDictionary<Guid, long> Last = new();

    /// <summary>True when this process has not recorded activity for the person in the last touch window, so a database write is worth doing.</summary>
    public static bool Due(Guid id)
    {
        var now = Environment.TickCount64;
        var window = (long)TouchEvery.TotalMilliseconds;
        while (true)
        {
            if (!Last.TryGetValue(id, out var prev))
            {
                if (Last.TryAdd(id, now)) return true;
                continue;
            }
            if (now - prev < window) return false;
            if (Last.TryUpdate(id, now, prev)) return true;
        }
    }
}
