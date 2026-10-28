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

    [Theory]
    [InlineData("2026-11-08T10:30:00", "Europe/Berlin", 10, 30, 1)] // CET UTC+1
    [InlineData("2026-07-01T10:30:00", "Europe/Berlin", 10, 30, 2)] // CEST UTC+2
    [InlineData("2026-11-08T10:30:00", null, 10, 30, 1)] // default Europe/Berlin
    public void TryParseEventDateTimeOffset_OffsetLess_UsesWallClockInTimeZone(
        string input,
        string? timeZoneId,
        int hour,
        int minute,
        int offsetHours)
    {
        Assert.True(CalDavCalendarService.TryParseEventDateTimeOffset(input, timeZoneId, out var dto));

        Assert.Equal(hour, dto.Hour);
        Assert.Equal(minute, dto.Minute);
        Assert.Equal(TimeSpan.FromHours(offsetHours), dto.Offset);

        var (local, _, tz) = CalDavCalendarService.ResolveEventDateTimes(dto, dto.AddHours(1), timeZoneId);
        Assert.Equal(string.IsNullOrWhiteSpace(timeZoneId) ? CalDavCalendarService.DefaultTimeZoneId : timeZoneId, tz);
        Assert.Equal(hour, local.Hour);
        Assert.Equal(minute, local.Minute);
    }

    [Fact]
    public void TryParseEventDateTimeOffset_ExplicitOffset_PreservesInstant()
    {
        Assert.True(CalDavCalendarService.TryParseEventDateTimeOffset(
            "2026-11-08T10:30:00+01:00",
            "Europe/Berlin",
            out var berlin));
        Assert.Equal(10, berlin.Hour);
        Assert.Equal(TimeSpan.FromHours(1), berlin.Offset);

        Assert.True(CalDavCalendarService.TryParseEventDateTimeOffset(
            "2026-11-08T09:30:00Z",
            "Europe/Berlin",
            out var utc));
        var (local, _, _) = CalDavCalendarService.ResolveEventDateTimes(utc, utc.AddHours(1), "Europe/Berlin");
        Assert.Equal(10, local.Hour);
        Assert.Equal(30, local.Minute);
    }

    [Fact]
    public void TryParseEventDateTimeOffset_DstSpringForward_SkipsInvalidLocalTime()
    {
        // 2026-03-29 02:00–02:59 does not exist in Europe/Berlin (CET→CEST).
        Assert.True(CalDavCalendarService.TryParseEventDateTimeOffset(
            "2026-03-29T02:30:00",
            "Europe/Berlin",
            out var dto));

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.False(tz.IsInvalidTime(dto.DateTime));
        Assert.Equal(TimeSpan.FromHours(2), dto.Offset); // landed in CEST
        Assert.True(dto.Hour >= 3);
    }

    [Fact]
    public void TryParseEventDateTimeOffset_DstFallBack_PicksAmbiguousOffset()
    {
        // 2026-10-25 02:30 is ambiguous in Europe/Berlin (CEST→CET).
        Assert.True(CalDavCalendarService.TryParseEventDateTimeOffset(
            "2026-10-25T02:30:00",
            "Europe/Berlin",
            out var dto));

        Assert.Equal(2, dto.Hour);
        Assert.Equal(30, dto.Minute);
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Contains(dto.Offset, tz.GetAmbiguousTimeOffsets(dto.DateTime));
    }

    [Theory]
    [InlineData("2026-11-08T10:30:00", false)]
    [InlineData("2026-11-08T10:30:00Z", true)]
    [InlineData("2026-11-08T10:30:00+01:00", true)]
    [InlineData("2026-11-08T10:30:00-05:00", true)]
    [InlineData("2026-11-08", false)]
    public void HasExplicitTimeZoneOffset_DetectsOffsetMarkers(string value, bool expected) =>
        Assert.Equal(expected, CalDavCalendarService.HasExplicitTimeZoneOffset(value));

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
