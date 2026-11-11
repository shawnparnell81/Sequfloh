using System.Globalization;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// The shop day is kept in America/New_York. The scheduled end is a clock time (4:00 PM by default), not a duration,
/// so a sweep that runs late still closes the day at that clock time.
/// </summary>
public static class DayClockTime
{
    public const string ZoneId = "America/New_York";

    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    public static bool TryParse(string? text, out TimeOnly end)
    {
        end = new TimeOnly(16, 0);
        if (string.IsNullOrWhiteSpace(text)) return true;
        return TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm", "h:mm tt", "hh:mm tt"], CultureInfo.InvariantCulture, DateTimeStyles.None, out end)
            || TimeOnly.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out end);
    }

    /// <summary>The instant a local civil time maps to. Invalid gaps (the spring-forward hour) move forward a minute. The earlier offset is used when the clock falls back.</summary>
    public static DateTimeOffset AtLocal(DateTime local, TimeZoneInfo? zone = null)
    {
        zone ??= Zone;
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local)[0] : zone.GetUtcOffset(local);
        // PostgreSQL timestamptz is stored as UTC. A non-zero offset cannot be written.
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// When this attendance must end. A start before the end time closes that same local day.
    /// A start after the end time (someone who came in for the evening) closes at the next day's end time.
    /// Working late moves the deadline by the hard cap.
    /// </summary>
    public static DateTimeOffset Deadline(DateTimeOffset started, bool workingLate, TimeOnly end, int hardCapMinutes, TimeZoneInfo? zone = null)
    {
        zone ??= Zone;
        var local = TimeZoneInfo.ConvertTime(started, zone);
        var endLocal = local.Date + end.ToTimeSpan();
        if (local.DateTime >= endLocal) endLocal = endLocal.AddDays(1);
        var scheduled = AtLocal(endLocal, zone);
        if (!workingLate) return scheduled;
        var cap = Math.Clamp(hardCapMinutes, 0, 12 * 60);
        return scheduled.AddMinutes(cap);
    }

    public static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone ?? Zone).DateTime);

    public static (DateTimeOffset Start, DateTimeOffset End) LocalDay(DateOnly day, TimeZoneInfo? zone = null)
    {
        zone ??= Zone;
        var start = AtLocal(day.ToDateTime(TimeOnly.MinValue), zone);
        var end = AtLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        return (start, end);
    }

    public static string Stamp(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Zone);
        return local.ToString("MMMM d, yyyy 'at' h:mm tt", CultureInfo.GetCultureInfo("en-US")) + " ET";
    }

    public static string Clock(TimeOnly end) =>
        DateTime.Today.Add(end.ToTimeSpan()).ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));
}
