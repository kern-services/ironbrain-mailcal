namespace Ironbrain.Calendar;

/// <summary>
/// A single calendar appointment. <see cref="Start"/> and <see cref="End"/> are always in UTC.
/// </summary>
public sealed class CalendarAppointment
{
    public string Summary { get; set; } = string.Empty;

    /// <summary>Start time in UTC.</summary>
    public DateTime Start { get; set; }

    /// <summary>End time in UTC.</summary>
    public DateTime End { get; set; }

    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>iCalendar UID when available.</summary>
    public string? Uid { get; set; }

    /// <summary>CalDAV resource href when known (needed for update/delete).</summary>
    public string? Href { get; set; }

    /// <summary>Display name of the calendar collection this event came from.</summary>
    public string? CalendarName { get; set; }

    /// <summary>Collection path segment / id (e.g. <c>personal</c>, <c>Family</c>).</summary>
    public string? CalendarId { get; set; }

    /// <summary>Full CalDAV collection URL when known.</summary>
    public string? CalendarHref { get; set; }
}

/// <summary>A discovered or configured CalDAV calendar collection.</summary>
public sealed class CalendarCollectionInfo
{
    public required string DisplayName { get; init; }
    public required string Href { get; init; }
    public required string CollectionId { get; init; }
    public bool IsShared { get; init; }
}

/// <summary>Options for multi-calendar appointment queries.</summary>
public sealed class CalendarQueryOptions
{
    /// <summary>Single collection or .ics URL override — skips multi-calendar discovery.</summary>
    public string? CalendarUrl { get; set; }

    /// <summary>
    /// Filter by display name and/or collection id (case-insensitive). Empty = all calendars
    /// (subject to shared include/exclude).
    /// </summary>
    public IReadOnlyList<string>? CalendarFilters { get; set; }

    /// <summary>
    /// When set, overrides <see cref="CalendarOptions.IncludeSharedByDefault"/>.
    /// </summary>
    public bool? IncludeShared { get; set; }

    /// <summary>
    /// Optional host calendar account id. When null, the host uses the first calendar config.
    /// </summary>
    public string? AccountId { get; set; }
}

/// <summary>Request to create a new calendar event via CalDAV PUT.</summary>
public sealed class CalendarAddRequest
{
    public required string Summary { get; set; }
    public required DateTime Start { get; set; }
    public required DateTime End { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }

    /// <summary>Optional IANA timezone (e.g. Europe/Berlin). When set, the event is stored in this timezone.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Optional override of the calendar collection URL.</summary>
    public string? CalendarUrl { get; set; }

    /// <summary>
    /// Calendar display name or collection id for writes (required when multiple calendars exist
    /// and <see cref="CalendarUrl"/> / <see cref="CalendarOptions.DefaultWriteCalendar"/> are unset).
    /// </summary>
    public string? CalendarSelector { get; set; }

    /// <summary>
    /// Optional host calendar account id. When null, the host uses the first calendar config.
    /// </summary>
    public string? AccountId { get; set; }
}
