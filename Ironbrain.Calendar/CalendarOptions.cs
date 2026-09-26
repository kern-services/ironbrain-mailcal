namespace Ironbrain.Calendar;

/// <summary>
/// Configuration for a CalDAV / iCalendar source (any self-hosted server, e.g. Mailcow SOGo).
/// </summary>
public sealed class CalendarOptions
{
    public const string SectionName = "Calendar";

    /// <summary>
    /// Full CalDAV calendar collection URL, or a direct <c>.ics</c> / webcal feed URL (read-only).
    /// Example (SOGo / Mailcow): <c>https://mail.example.com/SOGo/dav/user@domain/Calendar/personal/</c>
    /// When multi-calendar listing is used without <see cref="HomeUrl"/>, the parent of this URL
    /// (e.g. <c>…/Calendar/</c>) is used as the discovery home.
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional CalDAV calendar-home / parent collection URL for discovery
    /// (e.g. <c>https://mail.example.com/SOGo/dav/user@domain/Calendar/</c>).
    /// When empty, inferred as the parent of <see cref="SourceUrl"/>.
    /// </summary>
    public string HomeUrl { get; set; } = string.Empty;

    /// <summary>
    /// Username for HTTP Basic authentication (CalDAV). Used when querying the configured SourceUrl.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Password for HTTP Basic authentication (CalDAV). Used when querying the configured SourceUrl.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// When true, treat <see cref="SourceUrl"/> as a single iCalendar feed (HTTP GET) instead of CalDAV REPORT.
    /// Auto-detected when the URL ends with <c>.ics</c> or uses the <c>webcal://</c> scheme.
    /// </summary>
    public bool PreferIcsFeed { get; set; }

    /// <summary>
    /// Optional explicit calendar list (skips PROPFIND discovery when non-empty).
    /// </summary>
    public List<CalendarConfigEntry> Calendars { get; set; } = [];

    /// <summary>
    /// Display name or collection id used as the default target for <c>cal add</c> when <c>--calendar</c> is omitted.
    /// </summary>
    public string DefaultWriteCalendar { get; set; } = string.Empty;

    /// <summary>
    /// When false, discovered shared calendars are omitted unless the caller passes includeShared=true.
    /// Default true: include all discovered collections.
    /// </summary>
    public bool IncludeSharedByDefault { get; set; } = true;
}

/// <summary>Optional named calendar collection in config.</summary>
public sealed class CalendarConfigEntry
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
