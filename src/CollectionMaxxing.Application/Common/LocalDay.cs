using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Common;

/// <summary>
/// The calendar day the person is in. The server runs on UTC and knows nothing of the person's time zone, so an item added at
/// 8 pm US Central would be dated tomorrow. The phone's UTC offset (sent when it starts a scan session) is what lets the server
/// date what it adds on its own, such as a scan commit or an auto-add, the way the phone dates what it adds itself.
/// </summary>
public static class LocalDay
{
    /// <summary>Whether <paramref name="minutes"/> is an offset some time zone has: from UTC−12:00 to UTC+14:00.</summary>
    public static bool IsRealOffset(int minutes) => minutes is >= ScanSession.MinUtcOffsetMinutes and <= ScanSession.MaxUtcOffsetMinutes;

    /// <summary>
    /// The day on a clock <paramref name="offsetMinutes"/> from UTC at <paramref name="now"/>; the UTC day when the phone sent no
    /// offset (an older app) or one that isn't real.
    /// </summary>
    public static DateOnly Of(DateTimeOffset now, int? offsetMinutes) =>
        DateOnly.FromDateTime(offsetMinutes is { } minutes && IsRealOffset(minutes)
            ? now.ToOffset(TimeSpan.FromMinutes(minutes)).DateTime
            : now.UtcDateTime);

    /// <summary>
    /// The latest day a phone may name as today: one day past the UTC day, which is where UTC+14 already is. A wrong phone clock
    /// can't stamp a day further ahead than any time zone could be.
    /// </summary>
    public static DateOnly Latest(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
}
