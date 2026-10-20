using Ical.Net;
using Xunit;

namespace Ironbrain.Calendar.Tests;

public class CalendarAddRecurrenceTimezoneTests
{
    [Fact]
    public void ResolveEventDateTimes_OmitsTimeZone_DefaultsToEuropeBerlin()
    {
        var start = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.FromHours(2));
        var end = start.AddHours(1);

        var (startLocal, endLocal, tz) = CalDavCalendarService.ResolveEventDateTimes(start, end, timeZoneId: null);

        Assert.Equal(CalDavCalendarService.DefaultTimeZoneId, tz);
        Assert.Equal(DateTimeKind.Unspecified, startLocal.Kind);
        Assert.Equal(14, startLocal.Hour);
        Assert.Equal(15, endLocal.Hour);
    }

    [Fact]
    public void ResolveEventDateTimes_KeepsOffsetViaConversion()
    {
        // 12:00 UTC → 14:00 Europe/Berlin (CEST)
        var start = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);

        var (startLocal, _, tz) = CalDavCalendarService.ResolveEventDateTimes(start, end, "Europe/Berlin");

        Assert.Equal("Europe/Berlin", tz);
        Assert.Equal(14, startLocal.Hour);
    }

    [Fact]
    public void BuildRecurrencePattern_FromRrule()
    {
        var pattern = CalDavCalendarService.BuildRecurrencePattern(
            "FREQ=WEEKLY;COUNT=4", null, null, null);
        Assert.NotNull(pattern);
        Assert.Equal(FrequencyType.Weekly, pattern!.Frequency);
        Assert.Equal(4, pattern.Count);
    }

    [Fact]
    public void BuildRecurrencePattern_WeeklyWithCount()
    {
        var pattern = CalDavCalendarService.BuildRecurrencePattern(
            null, "weekly", recurrenceCount: 8, recurrenceUntil: null);
        Assert.NotNull(pattern);
        Assert.Equal(FrequencyType.Weekly, pattern!.Frequency);
        Assert.Equal(8, pattern.Count);
    }

    [Fact]
    public void BuildRecurrencePattern_FrequencyWithoutEnd_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CalDavCalendarService.BuildRecurrencePattern(null, "daily", null, null));
    }
}
