namespace Ironbrain.Calendar;

/// <summary>
/// Reads and writes appointments via CalDAV (RFC 4791) or a plain iCalendar (.ics) feed.
/// </summary>
public interface ICalendarService
{
    /// <summary>Lists CalDAV calendar collections under the account calendar home (or configured list).</summary>
    /// <param name="accountId">Optional host calendar account id; null → first calendar config.</param>
    Task<IReadOnlyList<CalendarCollectionInfo>> ListCalendarsAsync(
        string? userId = null,
        bool? includeShared = null,
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <param name="userId">When set, uses this user's calendar config from the host; otherwise uses appsettings / file config.</param>
    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForDayAsync(
        string date,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForDayAsync(
        string date,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForWeekAsync(
        string startDate,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForWeekAsync(
        string startDate,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsInRangeAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsInRangeAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a new VEVENT via CalDAV PUT. Not supported for read-only .ics feeds.</summary>
    Task<string> AddAppointmentAsync(
        CalendarAddRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
