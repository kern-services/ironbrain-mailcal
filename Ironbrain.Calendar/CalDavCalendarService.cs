using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ironbrain.Calendar;

/// <summary>
/// CalDAV (RFC 4791) and plain iCalendar feed client for arbitrary self-hosted servers.
/// Supports multi-calendar discovery (PROPFIND) and merged REPORT queries.
/// </summary>
public sealed class CalDavCalendarService(
    IHttpClientFactory httpClientFactory,
    IOptions<CalendarOptions> options,
    IServiceProvider serviceProvider,
    ILogger<CalDavCalendarService> logger) : ICalendarService
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    public async Task<IReadOnlyList<CalendarCollectionInfo>> ListCalendarsAsync(
        string? userId = null,
        bool? includeShared = null,
        CancellationToken cancellationToken = default)
    {
        var opts = await ResolveOptionsAsync(userId, cancellationToken).ConfigureAwait(false);
        var all = await DiscoverCalendarsAsync(opts, useAuth: true, cancellationToken).ConfigureAwait(false);
        var include = includeShared ?? opts.IncludeSharedByDefault;
        return CalendarCollectionHelper.ApplyFilters(all, filters: null, includeShared: include);
    }

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForDayAsync(
        string date,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default) =>
        GetAppointmentsForDayAsync(date, new CalendarQueryOptions { CalendarUrl = calendarUrl }, userId, cancellationToken);

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForDayAsync(
        string date,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = ResolveDayRange(date);
        return GetAppointmentsInRangeAsync(start, end, query, userId, cancellationToken);
    }

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForWeekAsync(
        string startDate,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default) =>
        GetAppointmentsForWeekAsync(startDate, new CalendarQueryOptions { CalendarUrl = calendarUrl }, userId, cancellationToken);

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsForWeekAsync(
        string startDate,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var (start, _) = ResolveDayRange(startDate);
        return GetAppointmentsInRangeAsync(start, start.AddDays(7), query, userId, cancellationToken);
    }

    public Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsInRangeAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        string? calendarUrl = null,
        string? userId = null,
        CancellationToken cancellationToken = default) =>
        GetAppointmentsInRangeAsync(rangeStart, rangeEnd, new CalendarQueryOptions { CalendarUrl = calendarUrl }, userId, cancellationToken);

    public async Task<IReadOnlyList<CalendarAppointment>> GetAppointmentsInRangeAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        CalendarQueryOptions query,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var opts = await ResolveOptionsAsync(userId, cancellationToken).ConfigureAwait(false);

        // Explicit single URL / .ics override
        if (!string.IsNullOrWhiteSpace(query.CalendarUrl))
        {
            var (url, useAuth) = ResolveUrl(query.CalendarUrl, opts);
            if (ShouldUseIcsFeed(url, opts))
                return await GetFromIcsFeedAsync(url, useAuth, opts, rangeStart, rangeEnd, calendarMeta: null, cancellationToken)
                    .ConfigureAwait(false);

            var id = CalendarCollectionHelper.CollectionIdFromHref(url);
            var meta = new CalendarCollectionInfo
            {
                DisplayName = id,
                Href = url,
                CollectionId = id,
                IsShared = false
            };
            return await GetFromCalDavAsync(url, useAuth: true, opts, rangeStart, rangeEnd, meta, cancellationToken)
                .ConfigureAwait(false);
        }

        // PreferIcsFeed with SourceUrl only — single feed
        var source = opts.SourceUrl?.Trim() ?? "";
        if (ShouldUseIcsFeed(CalendarCollectionHelper.NormalizeUrl(string.IsNullOrEmpty(source) ? "x" : source), opts)
            || (!string.IsNullOrEmpty(source) && source.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)))
        {
            var (url, useAuth) = ResolveUrl(null, opts);
            return await GetFromIcsFeedAsync(url, useAuth, opts, rangeStart, rangeEnd, calendarMeta: null, cancellationToken)
                .ConfigureAwait(false);
        }

        var discovered = await DiscoverCalendarsAsync(opts, useAuth: true, cancellationToken).ConfigureAwait(false);
        var includeShared = query.IncludeShared ?? opts.IncludeSharedByDefault;
        var targets = CalendarCollectionHelper.ApplyFilters(discovered, query.CalendarFilters, includeShared);

        if (targets.Count == 0)
            return [];

        var tasks = targets.Select(cal =>
            GetFromCalDavAsync(cal.Href, useAuth: true, opts, rangeStart, rangeEnd, cal, cancellationToken));
        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches.SelectMany(b => b).OrderBy(a => a.Start).ToList();
    }

    public async Task<string> AddAppointmentAsync(
        CalendarAddRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (request.End <= request.Start)
            throw new ArgumentException("End date-time must be after start date-time.", nameof(request));

        var opts = await ResolveOptionsAsync(userId, cancellationToken).ConfigureAwait(false);

        string url;
        bool useAuth;
        if (!string.IsNullOrWhiteSpace(request.CalendarUrl))
        {
            (url, useAuth) = ResolveUrl(request.CalendarUrl, opts);
        }
        else
        {
            if (ShouldUseIcsFeed(CalendarCollectionHelper.NormalizeUrl(opts.SourceUrl is { Length: > 0 } s ? s : "x"), opts)
                && string.IsNullOrWhiteSpace(opts.HomeUrl)
                && (opts.Calendars is null || opts.Calendars.Count == 0))
            {
                throw new InvalidOperationException("Cannot add events to a read-only iCalendar (.ics) feed. Configure a CalDAV collection URL instead.");
            }

            var collections = await DiscoverCalendarsAsync(opts, useAuth: true, cancellationToken).ConfigureAwait(false);
            // Writes: consider all calendars (including shared) so selector can target them explicitly
            var target = CalendarCollectionHelper.ResolveWriteTarget(
                collections,
                request.CalendarSelector,
                opts.DefaultWriteCalendar,
                explicitUrl: null);
            url = target.Href;
            useAuth = true;
        }

        if (ShouldUseIcsFeed(url, opts))
            throw new InvalidOperationException("Cannot add events to a read-only iCalendar (.ics) feed. Configure a CalDAV collection URL instead.");

        var ics = BuildIcsForNewEvent(
            request.Summary,
            request.Start,
            request.End,
            request.Location ?? string.Empty,
            request.Description ?? string.Empty,
            request.TimeZoneId?.Trim());
        var resourceName = Guid.NewGuid().ToString("N") + ".ics";
        var putUrl = CalendarCollectionHelper.EnsureCollectionUrl(url) + resourceName;

        var httpClient = CreateClient(useAuth, opts);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar")
        };

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException(
                $"CalDAV server returned {status} {reason} when adding appointment. Ensure the calendar URL is the full collection URL and the account has write access.");
        }

        logger.LogInformation("Added appointment '{Summary}' to calendar at {Url}", request.Summary, putUrl);
        return $"Appointment \"{request.Summary}\" added for {request.Start:yyyy-MM-dd HH:mm}–{request.End:HH:mm}.";
    }

    private async Task<IReadOnlyList<CalendarCollectionInfo>> DiscoverCalendarsAsync(
        CalendarOptions opts,
        bool useAuth,
        CancellationToken cancellationToken)
    {
        if (opts.Calendars is { Count: > 0 })
        {
            return opts.Calendars
                .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .Select(c =>
                {
                    var href = CalendarCollectionHelper.NormalizeUrl(c.Url);
                    var id = CalendarCollectionHelper.CollectionIdFromHref(href);
                    return new CalendarCollectionInfo
                    {
                        DisplayName = string.IsNullOrWhiteSpace(c.Name) ? id : c.Name.Trim(),
                        Href = href,
                        CollectionId = id,
                        IsShared = false
                    };
                })
                .ToList();
        }

        var home = opts.HomeUrl?.Trim();
        if (string.IsNullOrWhiteSpace(home))
            home = CalendarCollectionHelper.InferHomeUrl(opts.SourceUrl);

        if (string.IsNullOrWhiteSpace(home))
        {
            // Fall back to single SourceUrl as the only collection
            if (string.IsNullOrWhiteSpace(opts.SourceUrl))
                throw new InvalidOperationException(
                    "No calendar home or SourceUrl configured. Set Calendar:HomeUrl, Calendar:SourceUrl, or Calendar:Calendars.");

            var href = CalendarCollectionHelper.NormalizeUrl(opts.SourceUrl);
            if (ShouldUseIcsFeed(href, opts))
                throw new InvalidOperationException("Cannot discover CalDAV calendars from an .ics feed URL. Set Calendar:HomeUrl.");

            var id = CalendarCollectionHelper.CollectionIdFromHref(href);
            return
            [
                new CalendarCollectionInfo
                {
                    DisplayName = id,
                    Href = href,
                    CollectionId = id,
                    IsShared = false
                }
            ];
        }

        // Trailing slash required: SOGo may omit delegated calendars on Depth:1 without it.
        home = CalendarCollectionHelper.EnsureCollectionUrl(home);
        logger.LogInformation("CalDAV PROPFIND calendar discovery at {Home}", home);

        var httpClient = CreateClient(useAuth, opts);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), home)
        {
            Content = new StringContent(CalendarCollectionHelper.BuildPropfindXml(), Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            // Soft fallback: if SourceUrl is a collection, use it alone
            if (!string.IsNullOrWhiteSpace(opts.SourceUrl) && !ShouldUseIcsFeed(opts.SourceUrl, opts))
            {
                logger.LogWarning("PROPFIND {Status} at {Home}; falling back to Calendar:SourceUrl", status, home);
                var href = CalendarCollectionHelper.NormalizeUrl(opts.SourceUrl);
                var id = CalendarCollectionHelper.CollectionIdFromHref(href);
                return
                [
                    new CalendarCollectionInfo
                    {
                        DisplayName = id,
                        Href = href,
                        CollectionId = id,
                        IsShared = false
                    }
                ];
            }

            throw new InvalidOperationException(
                $"CalDAV PROPFIND returned {status} {reason} at calendar home {home}. Set Calendar:HomeUrl or Calendar:Calendars.");
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = CalendarCollectionHelper.ParsePropfindMultistatus(xml, home, opts.Username);
        if (parsed.Count > 0)
            return parsed;

        // Empty discovery but SourceUrl set → single collection
        if (!string.IsNullOrWhiteSpace(opts.SourceUrl) && !ShouldUseIcsFeed(opts.SourceUrl, opts))
        {
            var href = CalendarCollectionHelper.NormalizeUrl(opts.SourceUrl);
            var id = CalendarCollectionHelper.CollectionIdFromHref(href);
            return
            [
                new CalendarCollectionInfo
                {
                    DisplayName = id,
                    Href = href,
                    CollectionId = id,
                    IsShared = false
                }
            ];
        }

        return parsed;
    }

    private async Task<CalendarOptions> ResolveOptionsAsync(string? userId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            using var scope = serviceProvider.CreateScope();
            var provider = scope.ServiceProvider.GetService<IUserCalendarOptionsProvider>();
            if (provider is not null)
            {
                var fromUser = await provider.GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);
                if (fromUser is not null)
                    return fromUser;
            }
        }

        return options.Value;
    }

    private static (string Url, bool UseConfiguredAuth) ResolveUrl(string? calendarUrl, CalendarOptions opts)
    {
        var url = calendarUrl?.Trim();
        var useConfiguredAuth = false;
        if (string.IsNullOrEmpty(url))
        {
            url = opts.SourceUrl?.Trim();
            useConfiguredAuth = true;
        }

        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("No calendar URL provided. Set Calendar:SourceUrl in configuration or pass a calendar URL.");

        return (CalendarCollectionHelper.NormalizeUrl(url), useConfiguredAuth || calendarUrl is null);
    }

    private static bool ShouldUseIcsFeed(string url, CalendarOptions opts)
    {
        if (opts.PreferIcsFeed)
            return true;
        return url.EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Product token for CalDAV HTTP calls. SOGo omits delegated/shared calendar collections
    /// from Depth:1 PROPFIND when User-Agent is missing or empty (same URL/auth/body otherwise).
    /// </summary>
    internal const string CalDavUserAgent = "Ironbrain.MailCal/0.1";

    private HttpClient CreateClient(bool useAuth, CalendarOptions opts)
    {
        var httpClient = httpClientFactory.CreateClient(nameof(CalDavCalendarService));
        httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        // Must be non-empty: SOGo filters delegated calendars (e.g. alice_D_*_personal) when UA is blank.
        httpClient.DefaultRequestHeaders.UserAgent.Clear();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CalDavUserAgent);
        if (useAuth && !string.IsNullOrWhiteSpace(opts.Username))
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{opts.Username}:{opts.Password ?? ""}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return httpClient;
    }

    private async Task<IReadOnlyList<CalendarAppointment>> GetFromIcsFeedAsync(
        string url,
        bool useAuth,
        CalendarOptions opts,
        DateTime rangeStart,
        DateTime rangeEnd,
        CalendarCollectionInfo? calendarMeta,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching iCalendar feed {Url} for range {Start:O} to {End:O}", url, rangeStart, rangeEnd);
        var httpClient = CreateClient(useAuth, opts);
        using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException($"iCalendar feed returned {status} {reason}.");
        }

        var ics = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseOccurrences([ics], rangeStart, rangeEnd, calendarMeta);
    }

    private async Task<IReadOnlyList<CalendarAppointment>> GetFromCalDavAsync(
        string url,
        bool useAuth,
        CalendarOptions opts,
        DateTime rangeStart,
        DateTime rangeEnd,
        CalendarCollectionInfo? calendarMeta,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("CalDAV calendar-query to {Url} for range {Start:O} to {End:O}", url, rangeStart, rangeEnd);

        var httpClient = CreateClient(useAuth, opts);
        var startUtc = ToUtcForCalDavQuery(rangeStart);
        var endUtc = ToUtcForCalDavQuery(rangeEnd);
        var calendarQueryXml = BuildCalendarQueryXml(startUtc, endUtc);

        var reportUrl = CalendarCollectionHelper.EnsureCollectionUrl(url);
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), reportUrl)
        {
            Content = new StringContent(calendarQueryXml, Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException(
                $"CalDAV server returned {status} {reason}. Ensure Calendar:SourceUrl is the full CalDAV calendar collection URL (e.g. .../SOGo/dav/user@domain/Calendar/personal/).");
        }

        var reportXml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var icsParts = ExtractCalendarDataFromMultistatus(reportXml).ToList();
        if (icsParts.Count == 0)
        {
            logger.LogInformation("CalDAV report returned no calendar data for range {Start:O} to {End:O}", rangeStart, rangeEnd);
            return [];
        }

        return ParseOccurrences(icsParts, rangeStart, rangeEnd, calendarMeta);
    }

    private IReadOnlyList<CalendarAppointment> ParseOccurrences(
        IEnumerable<string> icsParts,
        DateTime rangeStart,
        DateTime rangeEnd,
        CalendarCollectionInfo? calendarMeta)
    {
        var searchStart = ToCalDateTime(rangeStart);
        var rangeEndCmp = AsUnspecifiedOrUtc(rangeEnd);
        var occurrences = new List<(Ical.Net.DataTypes.Occurrence Occ, CalendarEvent? Evt)>();
        foreach (var ics in icsParts)
        {
            var calendar = IcalCalendar.Load(ics);
            if (calendar?.Events is null)
                continue;
            foreach (var ev in calendar.Events)
            {
                if (ev is not CalendarEvent calEvt)
                    continue;
                try
                {
                    var evOccurrences = calEvt.GetOccurrences(searchStart, null)
                        .TakeWhile(o => o.Period?.StartTime?.Value is { } t
                            && AsUnspecifiedOrUtc(t) < rangeEndCmp);
                    foreach (var occ in evOccurrences)
                        occurrences.Add((occ, calEvt));
                }
                catch (Ical.Net.Evaluation.EvaluationOutOfRangeException ex)
                {
                    logger.LogWarning(ex, "Skipping event with unbounded recurrence (summary: {Summary})", calEvt.Summary ?? "(no summary)");
                }
            }
        }

        var list = new List<CalendarAppointment>();
        foreach (var (occ, evt) in occurrences)
        {
            var startTime = occ.Period?.StartTime;
            var endTime = occ.Period?.EndTime;
            var startDt = startTime?.AsUtc ?? AsUtcFallback(rangeStart);
            DateTime endDt;
            if (endTime?.AsUtc is { } explicitEnd && explicitEnd > startDt)
                endDt = explicitEnd;
            else if (evt?.End?.AsUtc is { } evtEnd && evtEnd > startDt)
                endDt = evtEnd;
            else
                endDt = startDt;

            list.Add(new CalendarAppointment
            {
                Summary = evt?.Summary ?? "(no title)",
                Start = startDt,
                End = endDt,
                Location = evt?.Location ?? string.Empty,
                Description = evt?.Description ?? string.Empty,
                Uid = evt?.Uid,
                CalendarName = calendarMeta?.DisplayName,
                CalendarId = calendarMeta?.CollectionId,
                CalendarHref = calendarMeta?.Href
            });
        }

        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return list;
    }

    internal static CalDateTime ToCalDateTime(DateTime value) =>
        new(AsUnspecifiedOrUtc(value));

    internal static DateTime AsUnspecifiedOrUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
        };

    private static DateTime AsUtcFallback(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static DateTime ToUtcForCalDavQuery(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };

    internal static (DateTime Start, DateTime End) ResolveDayRange(string date)
    {
        var d = date.Trim();
        if (d.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
            return (today, today.AddDays(1));
        }

        if (DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            var day = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            return (day, day.AddDays(1));
        }

        throw new ArgumentException($"Invalid date: '{date}'. Use yyyy-MM-dd or 'today'.", nameof(date));
    }

    public static DateTime ParseDateTime(string value, string paramName = "dateTime")
    {
        var v = value.Trim();
        var hasOffset = v.Contains('Z', StringComparison.OrdinalIgnoreCase) || v.Contains('+', StringComparison.Ordinal)
            || (v.Length >= 7 && v[^7] == '-' && char.IsDigit(v[^5]));
        if (hasOffset && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.UtcDateTime;

        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var dt))
            return dt;
        if (DateTime.TryParseExact(v, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return dt;
        if (DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return dt;
        throw new ArgumentException($"Invalid date-time: '{value}'. Use ISO 8601 (e.g. with +01:00 or Z) or yyyy-MM-dd HH:mm.", paramName);
    }

    private static string BuildCalendarQueryXml(DateTime startUtc, DateTime endUtc)
    {
        var startStr = startUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var endStr = endUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var query = new XElement(CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "C", CalDav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName),
            new XElement(Dav + "prop",
                new XElement(Dav + "getetag"),
                new XElement(CalDav + "calendar-data")),
            new XElement(CalDav + "filter",
                new XElement(CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(CalDav + "time-range", new XAttribute("start", startStr), new XAttribute("end", endStr))))));

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + query.ToString(SaveOptions.OmitDuplicateNamespaces);
    }

    private static IEnumerable<string> ExtractCalendarDataFromMultistatus(string multistatusXml)
    {
        var doc = XDocument.Parse(multistatusXml);
        foreach (var calData in doc.Descendants(CalDav + "calendar-data"))
        {
            var value = calData.Value;
            if (string.IsNullOrWhiteSpace(value))
                continue;
            yield return value.Trim();
        }
    }

    private static string BuildIcsForNewEvent(string summary, DateTime start, DateTime end, string location, string description, string? timeZoneId)
    {
        var uid = "ironbrain-" + Guid.NewGuid().ToString("N") + "@ironbrain";
        CalDateTime startCal;
        CalDateTime endCal;
        var calendar = new IcalCalendar();
        if (!string.IsNullOrEmpty(timeZoneId))
        {
            calendar.AddTimeZone(new VTimeZone(timeZoneId));
            startCal = new CalDateTime(AsUnspecifiedOrUtc(start), timeZoneId);
            endCal = new CalDateTime(AsUnspecifiedOrUtc(end), timeZoneId);
        }
        else
        {
            startCal = ToCalDateTime(start);
            endCal = ToCalDateTime(end);
        }

        var evt = new CalendarEvent
        {
            Summary = summary,
            Start = startCal,
            End = endCal,
            Uid = uid,
            Location = location,
            Description = description
        };
        calendar.Events.Add(evt);
        var serializer = new CalendarSerializer();
        var ics = serializer.SerializeToString(calendar);
        return ics ?? throw new InvalidOperationException("Failed to serialize calendar event to ICS.");
    }
}
