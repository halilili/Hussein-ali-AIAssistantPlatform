namespace Clinic.Api;

/// <summary>
/// Pure function: schedule + bookings + "now" → free slots. No I/O, trivially unit-testable.
/// Working hours are in clinic LOCAL time; everything returned carries the correct local offset
/// (Beirut switches between UTC+2 and UTC+3 for daylight saving).
/// </summary>
public sealed class SlotCalculator
{
    public static readonly TimeSpan MinimumLeadTime = TimeSpan.FromMinutes(30);

    public IReadOnlyList<SlotDto> Calculate(
        DateOnly date,
        TimeZoneInfo tz,
        int slotMinutes,
        IEnumerable<WorkingHoursRow> hours,
        IReadOnlySet<DateTimeOffset> bookedStarts,
        DateTimeOffset now)
    {
        var slots = new List<SlotDto>();
        foreach (var window in hours.OrderBy(h => h.StartMinute))
        {
            for (var m = window.StartMinute; m + slotMinutes <= window.EndMinute; m += slotMinutes)
            {
                var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(m);   // DateTimeKind.Unspecified
                var startsAt = new DateTimeOffset(local, tz.GetUtcOffset(local));

                if (startsAt < now + MinimumLeadTime) continue;      // no same-minute bookings
                if (bookedStarts.Contains(startsAt)) continue;       // DateTimeOffset equality = same instant

                slots.Add(new SlotDto(startsAt, startsAt.AddMinutes(slotMinutes)));
            }
        }
        return slots;
    }

    /// <summary>UTC range [from, to) covering one local calendar day.</summary>
    public static (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateOnly date, TimeZoneInfo tz)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (TimeZoneInfo.ConvertTimeToUtc(start, tz), TimeZoneInfo.ConvertTimeToUtc(end, tz));
    }
}
