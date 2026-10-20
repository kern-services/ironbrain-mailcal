using System.Net;
using System.Text;
using System.Xml.Linq;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.Extensions.Logging;
using IcalCalendar = Ical.Net.Calendar;

namespace Ironbrain.Calendar;

public sealed partial class CalDavCalendarService
{
    public async Task<string> UpdateAppointmentAsync(
        CalendarUpdateRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Uid))
            throw new ArgumentException("Uid is required.", nameof(request));
        if (request is { Start: { } s, End: { } e } && e <= s)
            throw new ArgumentException("End date-time must be after start date-time when both are provided.", nameof(request));
        if (request.Summary is null
            && request.Start is null
            && request.End is null
            && request.Location is null
            && request.Description is null)
        {
            throw new ArgumentException(
                "At least one field to update is required (summary, start, end, location, description).",
                nameof(request));
        }

        var opts = await ResolveOptionsAsync(userId, cancellationToken, request.AccountId).ConfigureAwait(false);
        var (collectionUrl, useAuth) = await ResolveWriteCollectionUrlAsync(request.CalendarUrl, request.CalendarSelector, opts, cancellationToken)
            .ConfigureAwait(false);

        var resource = await FindEventResourceByUidAsync(collectionUrl, useAuth, opts, request.Uid.Trim(), cancellationToken)
            .ConfigureAwait(false);
        var etag = string.IsNullOrWhiteSpace(request.ETag) ? resource.ETag : request.ETag.Trim();
        if (string.IsNullOrWhiteSpace(etag))
            throw new InvalidOperationException("Cannot update appointment: CalDAV resource has no ETag for If-Match.");

        var calendar = IcalCalendar.Load(resource.CalendarData)
            ?? throw new InvalidOperationException("Failed to parse existing calendar resource.");
        var uid = request.Uid.Trim();

        if (request.RecurrenceId is { } occurrenceStart)
        {
            ApplyOccurrenceUpdate(calendar, uid, occurrenceStart, request);
        }
        else
        {
            var master = FindMasterEvent(calendar, uid)
                ?? throw new InvalidOperationException($"No master VEVENT with UID '{uid}' found in calendar resource.");
            ApplyPartialFields(master, request);
            master.Sequence = master.Sequence + 1;
            master.DtStamp = new CalDateTime(DateTime.UtcNow);
        }

        var serializer = new CalendarSerializer();
        var ics = serializer.SerializeToString(calendar)
            ?? throw new InvalidOperationException("Failed to serialize updated calendar event to ICS.");

        await PutCalendarResourceAsync(resource.Href!, useAuth, opts, ics, etag!, cancellationToken).ConfigureAwait(false);

        var scope = request.RecurrenceId is null ? "series" : "occurrence";
        logger.LogInformation("Updated appointment UID {Uid} ({Scope}) at {Href}", uid, scope, resource.Href);
        return request.RecurrenceId is null
            ? $"Appointment UID '{uid}' updated (whole series)."
            : $"Appointment UID '{uid}' occurrence {request.RecurrenceId:yyyy-MM-dd HH:mm}Z updated.";
    }

    public async Task<string> DeleteAppointmentAsync(
        CalendarDeleteRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Uid))
            throw new ArgumentException("Uid is required.", nameof(request));

        var opts = await ResolveOptionsAsync(userId, cancellationToken, request.AccountId).ConfigureAwait(false);
        var (collectionUrl, useAuth) = await ResolveWriteCollectionUrlAsync(request.CalendarUrl, request.CalendarSelector, opts, cancellationToken)
            .ConfigureAwait(false);

        var resource = await FindEventResourceByUidAsync(collectionUrl, useAuth, opts, request.Uid.Trim(), cancellationToken)
            .ConfigureAwait(false);
        var etag = string.IsNullOrWhiteSpace(request.ETag) ? resource.ETag : request.ETag.Trim();
        if (string.IsNullOrWhiteSpace(etag))
            throw new InvalidOperationException("Cannot delete appointment: CalDAV resource has no ETag for If-Match.");

        var uid = request.Uid.Trim();

        if (request.RecurrenceId is null)
        {
            await DeleteCalendarResourceAsync(resource.Href!, useAuth, opts, etag!, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Deleted appointment series UID {Uid} at {Href}", uid, resource.Href);
            return $"Appointment UID '{uid}' deleted (whole series).";
        }

        var calendar = IcalCalendar.Load(resource.CalendarData)
            ?? throw new InvalidOperationException("Failed to parse existing calendar resource.");
        ApplyOccurrenceDelete(calendar, uid, request.RecurrenceId.Value);

        var serializer = new CalendarSerializer();
        var ics = serializer.SerializeToString(calendar)
            ?? throw new InvalidOperationException("Failed to serialize calendar after occurrence delete.");

        await PutCalendarResourceAsync(resource.Href!, useAuth, opts, ics, etag!, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Deleted appointment occurrence UID {Uid} at {RecurrenceId:o} ({Href})",
            uid,
            request.RecurrenceId,
            resource.Href);
        return $"Appointment UID '{uid}' occurrence {request.RecurrenceId:yyyy-MM-dd HH:mm}Z deleted.";
    }

    private async Task<(string Url, bool UseAuth)> ResolveWriteCollectionUrlAsync(
        string? calendarUrl,
        string? calendarSelector,
        CalendarOptions opts,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(calendarUrl))
        {
            var (url, useAuth) = ResolveUrl(calendarUrl, opts);
            if (ShouldUseIcsFeed(url, opts))
                throw new InvalidOperationException("Cannot modify events on a read-only iCalendar (.ics) feed. Configure a CalDAV collection URL instead.");
            return (url, useAuth);
        }

        if (ShouldUseIcsFeed(CalendarCollectionHelper.NormalizeUrl(opts.SourceUrl is { Length: > 0 } s ? s : "x"), opts)
            && string.IsNullOrWhiteSpace(opts.HomeUrl)
            && (opts.Calendars is null || opts.Calendars.Count == 0))
        {
            throw new InvalidOperationException("Cannot modify events on a read-only iCalendar (.ics) feed. Configure a CalDAV collection URL instead.");
        }

        var collections = await DiscoverCalendarsAsync(opts, useAuth: true, cancellationToken).ConfigureAwait(false);
        var target = CalendarCollectionHelper.ResolveWriteTarget(
            collections,
            calendarSelector,
            opts.DefaultWriteCalendar,
            explicitUrl: null);
        return (target.Href, true);
    }

    private async Task<CalDavResource> FindEventResourceByUidAsync(
        string collectionUrl,
        bool useAuth,
        CalendarOptions opts,
        string uid,
        CancellationToken cancellationToken)
    {
        var httpClient = CreateClient(useAuth, opts);
        var reportUrl = CalendarCollectionHelper.EnsureCollectionUrl(collectionUrl);
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), reportUrl)
        {
            Content = new StringContent(BuildUidQueryXml(uid), Encoding.UTF8, "application/xml")
        };
        request.Headers.Add("Depth", "1");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException($"CalDAV UID query returned {status} {reason} for UID '{uid}'.");
        }

        var reportXml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var resources = ExtractCalendarResourcesFromMultistatus(reportXml, reportUrl).ToList();
        if (resources.Count == 0)
            throw new InvalidOperationException($"No CalDAV resource found for UID '{uid}' in calendar '{collectionUrl}'.");

        // Prefer the resource that contains a matching UID (servers may return extras).
        foreach (var candidate in resources)
        {
            var cal = IcalCalendar.Load(candidate.CalendarData);
            if (cal?.Events is null)
                continue;
            if (cal.Events.Any(e => string.Equals(e.Uid, uid, StringComparison.Ordinal)))
            {
                if (string.IsNullOrWhiteSpace(candidate.Href))
                    throw new InvalidOperationException($"CalDAV resource for UID '{uid}' has no href.");
                return candidate;
            }
        }

        var first = resources[0];
        if (string.IsNullOrWhiteSpace(first.Href))
            throw new InvalidOperationException($"CalDAV resource for UID '{uid}' has no href.");
        return first;
    }

    private async Task PutCalendarResourceAsync(
        string href,
        bool useAuth,
        CalendarOptions opts,
        string ics,
        string etag,
        CancellationToken cancellationToken)
    {
        var httpClient = CreateClient(useAuth, opts);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, href)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar")
        };
        httpRequest.Headers.TryAddWithoutValidation("If-Match", etag);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException(
                $"CalDAV ETag conflict (412 Precondition Failed) updating '{href}'. Re-list the appointment and retry with the current ETag.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException($"CalDAV server returned {status} {reason} when updating appointment at '{href}'.");
        }
    }

    private async Task DeleteCalendarResourceAsync(
        string href,
        bool useAuth,
        CalendarOptions opts,
        string etag,
        CancellationToken cancellationToken)
    {
        var httpClient = CreateClient(useAuth, opts);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, href);
        httpRequest.Headers.TryAddWithoutValidation("If-Match", etag);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException(
                $"CalDAV ETag conflict (412 Precondition Failed) deleting '{href}'. Re-list the appointment and retry with the current ETag.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            throw new InvalidOperationException($"CalDAV server returned {status} {reason} when deleting appointment at '{href}'.");
        }
    }

    private static void ApplyOccurrenceUpdate(
        IcalCalendar calendar,
        string uid,
        DateTime occurrenceStartUtc,
        CalendarUpdateRequest request)
    {
        var master = FindMasterEvent(calendar, uid)
            ?? throw new InvalidOperationException($"No master VEVENT with UID '{uid}' found for occurrence update.");
        var rid = ToOccurrenceCalDateTime(occurrenceStartUtc, master);
        var overrideEvt = FindOverrideEvent(calendar, uid, occurrenceStartUtc);
        if (overrideEvt is null)
        {
            overrideEvt = CloneAsOccurrenceOverride(master, rid);
            calendar.Events.Add(overrideEvt);
        }

        ApplyPartialFields(overrideEvt, request);
        overrideEvt.RecurrenceIdentifier = new RecurrenceIdentifier(rid, range: null);
        overrideEvt.Sequence = Math.Max(master.Sequence, overrideEvt.Sequence) + 1;
        overrideEvt.DtStamp = new CalDateTime(DateTime.UtcNow);
    }

    private static void ApplyOccurrenceDelete(IcalCalendar calendar, string uid, DateTime occurrenceStartUtc)
    {
        var master = FindMasterEvent(calendar, uid)
            ?? throw new InvalidOperationException($"No master VEVENT with UID '{uid}' found for occurrence delete.");
        var overrideEvt = FindOverrideEvent(calendar, uid, occurrenceStartUtc);
        if (overrideEvt is not null)
            calendar.Events.Remove(overrideEvt);

        var rid = ToOccurrenceCalDateTime(occurrenceStartUtc, master);
        master.ExceptionDates.Add(rid);
        master.Sequence = master.Sequence + 1;
        master.DtStamp = new CalDateTime(DateTime.UtcNow);
    }

    private static CalendarEvent? FindMasterEvent(IcalCalendar calendar, string uid) =>
        calendar.Events
            .OfType<CalendarEvent>()
            .FirstOrDefault(e =>
                string.Equals(e.Uid, uid, StringComparison.Ordinal)
                && GetRecurrenceIdUtc(e) is null);

    private static CalendarEvent? FindOverrideEvent(IcalCalendar calendar, string uid, DateTime occurrenceStartUtc)
    {
        var target = AsUtcComparable(occurrenceStartUtc);
        return calendar.Events
            .OfType<CalendarEvent>()
            .FirstOrDefault(e =>
                string.Equals(e.Uid, uid, StringComparison.Ordinal)
                && GetRecurrenceIdUtc(e) is { } rid
                && AsUtcComparable(rid) == target);
    }

    private static CalendarEvent CloneAsOccurrenceOverride(CalendarEvent master, CalDateTime recurrenceId)
    {
        var duration = master.End is not null && master.Start is not null
            ? master.End.AsUtc - master.Start.AsUtc
            : TimeSpan.FromHours(1);
        var end = new CalDateTime(recurrenceId.AsUtc.Add(duration), recurrenceId.TzId);

        return new CalendarEvent
        {
            Uid = master.Uid,
            Summary = master.Summary,
            Location = master.Location,
            Description = master.Description,
            Start = recurrenceId,
            End = end,
            RecurrenceIdentifier = new RecurrenceIdentifier(recurrenceId, range: null),
            Sequence = master.Sequence,
            DtStamp = new CalDateTime(DateTime.UtcNow)
        };
    }

    private static void ApplyPartialFields(CalendarEvent evt, CalendarUpdateRequest request)
    {
        if (request.Summary is not null)
            evt.Summary = request.Summary;
        if (request.Location is not null)
            evt.Location = request.Location;
        if (request.Description is not null)
            evt.Description = request.Description;

        var tz = request.TimeZoneId?.Trim();
        var previousStart = evt.Start;
        var previousEnd = evt.End;

        if (request.Start is { } start)
        {
            evt.Start = string.IsNullOrEmpty(tz)
                ? ToCalDateTime(start)
                : new CalDateTime(AsUnspecifiedOrUtc(start), tz);
        }

        if (request.End is { } end)
        {
            evt.End = string.IsNullOrEmpty(tz)
                ? ToCalDateTime(end)
                : new CalDateTime(AsUnspecifiedOrUtc(end), tz);
        }
        else if (request.Start is not null && previousStart is not null && previousEnd is not null)
        {
            var duration = previousEnd.AsUtc - previousStart.AsUtc;
            if (duration > TimeSpan.Zero && evt.Start is not null)
            {
                var tzId = evt.Start.TzId ?? tz;
                evt.End = string.IsNullOrEmpty(tzId)
                    ? ToCalDateTime(evt.Start.AsUtc.Add(duration))
                    : new CalDateTime(evt.Start.AsUtc.Add(duration), tzId);
            }
        }
    }

    private static CalDateTime ToOccurrenceCalDateTime(DateTime occurrenceStartUtc, CalendarEvent master)
    {
        var utc = AsUtcComparable(occurrenceStartUtc);
        if (!string.IsNullOrEmpty(master.Start?.TzId))
            return new CalDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), master.Start.TzId);
        return new CalDateTime(utc, "UTC");
    }

    private static DateTime AsUtcComparable(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static string BuildUidQueryXml(string uid)
    {
        var query = new XElement(CalDav + "calendar-query",
            new XAttribute(XNamespace.Xmlns + "C", CalDav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName),
            new XElement(Dav + "prop",
                new XElement(Dav + "getetag"),
                new XElement(CalDav + "calendar-data")),
            new XElement(CalDav + "filter",
                new XElement(CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"),
                    new XElement(CalDav + "comp-filter", new XAttribute("name", "VEVENT"),
                        new XElement(CalDav + "prop-filter", new XAttribute("name", "UID"),
                            new XElement(CalDav + "text-match",
                                new XAttribute("collation", "i;octet"),
                                uid))))));

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + query.ToString(SaveOptions.OmitDuplicateNamespaces);
    }
}
