using System.ComponentModel;
using System.Text.Json;
using Ironbrain.Calendar;
using Ironbrain.Email;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace Ironbrain.MailCal.Mcp;

/// <summary>
/// Thin MCP tool surface over MailCal accounts (multi-account aware).
/// Pass optional <c>account</c> to select; same defaulting as the CLI.
/// </summary>
[McpServerToolType]
public sealed class MailCalTools(
    AccountCatalog catalog,
    MailCalClientFactory clientFactory,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool(Name = "list_accounts")]
    [Description("Lists configured mail/calendar account names with hosts/usernames (passwords never included).")]
    public string ListAccounts() =>
        JsonSerializer.Serialize(catalog.ListSummaries(), JsonOptions);

    [McpServerTool(Name = "list_calendars")]
    [Description("Lists CalDAV calendar collections for the selected account (displayname, href/id, shared). Passwords never included.")]
    public async Task<string> ListCalendarsAsync(
        [Description("Optional account name; defaultAccount / IRONBRAIN_MAILCAL_ACCOUNT / sole account if omitted")] string? account = null,
        [Description("When set, overrides Calendar:IncludeSharedByDefault for shared/delegated calendars")] bool? includeShared = null,
        CancellationToken cancellationToken = default)
    {
        var (_, calendar, _) = Create(account);
        var items = await calendar.ListCalendarsAsync(userId: null, includeShared: includeShared, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerTool(Name = "list_emails")]
    [Description(
        "Lists emails from the selected account's IMAP mailbox with optional server-side filters. " +
        "Returns items, totalCount, offset, limit, nextOffset.")]
    public async Task<string> ListEmailsAsync(
        [Description("Optional account name; defaultAccount / IRONBRAIN_MAILCAL_ACCOUNT / sole account if omitted")] string? account = null,
        [Description("Max number of emails (1-100)")] int limit = 20,
        [Description("Offset from the most recent")] int offset = 0,
        [Description("Optional IMAP mailbox/folder override")] string? mailbox = null,
        [Description("When true, only unread; when false, only read")] bool? unread = null,
        [Description("Inclusive lower bound (ISO-8601) on delivery date")] DateTimeOffset? since = null,
        [Description("Exclusive upper bound (ISO-8601) on delivery date")] DateTimeOffset? before = null,
        [Description("Substring match on From")] string? fromContains = null,
        [Description("Substring match on Subject")] string? subjectContains = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, _) = Create(account, mailbox);
        EmailListFilter? filter = null;
        if (unread.HasValue || since.HasValue || before.HasValue
            || !string.IsNullOrWhiteSpace(fromContains) || !string.IsNullOrWhiteSpace(subjectContains))
        {
            filter = new EmailListFilter
            {
                Unread = unread,
                Since = since,
                Before = before,
                FromContains = fromContains,
                SubjectContains = subjectContains
            };
        }

        var result = await email.ListInboxAsync(limit, offset, userId: null, cancellationToken, mailbox, filter: filter);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "get_email")]
    [Description(
        "Gets a specific email by IMAP unique id from the selected account. " +
        "Includes In-Reply-To and References for threading verification.")]
    public async Task<string> GetEmailAsync(
        [Description("IMAP unique id")] string id,
        [Description("Optional account name")] string? account = null,
        [Description("Optional IMAP mailbox/folder override")] string? mailbox = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, _) = Create(account, mailbox);
        var content = await email.GetEmailAsync(id, userId: null, cancellationToken, mailbox);
        return JsonSerializer.Serialize(content, JsonOptions);
    }

    [McpServerTool(Name = "send_email")]
    [Description("Sends an email via the selected account's SMTP. Also appends to IMAP Sent when configured.")]
    public async Task<string> SendEmailAsync(
        [Description("Recipient email address")] string to,
        [Description("Subject")] string subject,
        [Description("Plain text body")] string? textBody = null,
        [Description("HTML body")] string? htmlBody = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, resolved) = Create(account);
        resolved.EnsureSendAllowed();
        await email.SendEmailAsync(new EmailSendRequest
        {
            To = to,
            Subject = subject,
            TextBody = textBody,
            HtmlBody = htmlBody
        }, userId: null, cancellationToken);
        return $"Sent email to {to} with subject '{subject}' (account={resolved.Name}).";
    }

    [McpServerTool(Name = "move_email")]
    [Description(
        "Moves one or more messages by IMAP UID to a destination mailbox. " +
        "Pass uid for a single message, or uids (comma-separated) for bulk. " +
        "Prefers UID MOVE; falls back to COPY+\\Deleted+EXPUNGE.")]
    public async Task<string> MoveEmailAsync(
        [Description("IMAP unique id (UID); omit when uids is set")] string? uid = null,
        [Description("Destination IMAP mailbox (e.g. Archive/2026)")] string toMailbox = "",
        [Description("Optional source mailbox override (default: account Imap.Mailbox / INBOX)")] string? mailbox = null,
        [Description("Optional account name")] string? account = null,
        [Description("Comma-separated IMAP UIDs for bulk move (one IMAP MOVE)")] string? uids = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, resolved) = Create(account, mailbox);
        var idList = ParseUidList(uid, uids);
        if (idList.Count == 1)
        {
            var result = await email.MoveEmailAsync(idList[0], toMailbox, sourceMailbox: null, userId: null, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                result.Id,
                result.FromMailbox,
                result.ToMailbox,
                result.Method,
                account = resolved.Name
            }, JsonOptions);
        }

        var bulk = await email.MoveEmailsAsync(idList, toMailbox, sourceMailbox: null, userId: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            bulk.Ids,
            bulk.FromMailbox,
            bulk.ToMailbox,
            bulk.Method,
            bulk.MovedCount,
            account = resolved.Name
        }, JsonOptions);
    }

    [McpServerTool(Name = "archive_email")]
    [Description(
        "Archives one or more messages by IMAP UID into Email:Imap:ArchiveFolder. " +
        "Pass uid for a single message, or uids (comma-separated) for bulk.")]
    public async Task<string> ArchiveEmailAsync(
        [Description("IMAP unique id (UID); omit when uids is set")] string? uid = null,
        [Description("Optional source mailbox override (default: account Imap.Mailbox / INBOX)")] string? mailbox = null,
        [Description("Optional account name")] string? account = null,
        [Description("Comma-separated IMAP UIDs for bulk archive")] string? uids = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, resolved) = Create(account, mailbox);
        var idList = ParseUidList(uid, uids);
        if (idList.Count == 1)
        {
            var result = await email.ArchiveEmailAsync(idList[0], sourceMailbox: null, userId: null, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                result.Id,
                result.FromMailbox,
                result.ToMailbox,
                result.Method,
                account = resolved.Name
            }, JsonOptions);
        }

        var bulk = await email.ArchiveEmailsAsync(idList, sourceMailbox: null, userId: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            groups = bulk,
            account = resolved.Name
        }, JsonOptions);
    }

    private static IReadOnlyList<string> ParseUidList(string? uid, string? uids)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(uids))
        {
            foreach (var part in uids.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(part);
        }

        if (!string.IsNullOrWhiteSpace(uid))
            list.Insert(0, uid.Trim());

        if (list.Count == 0)
            throw new ArgumentException("Provide uid and/or uids.");

        return list;
    }

    [McpServerTool(Name = "get_appointments_for_day")]
    [Description("Gets calendar appointments for a day. Default: all discovered calendars for the account. Each appointment includes calendarName/calendarId.")]
    public async Task<string> GetAppointmentsForDayAsync(
        [Description("Date yyyy-MM-dd or 'today'")] string date = "today",
        [Description("Optional single calendar URL override (skips multi-calendar discovery)")] string? calendarUrl = null,
        [Description("Optional calendar filters: display names or collection ids, comma-separated (e.g. Family,personal)")] string? calendars = null,
        [Description("When set, overrides Calendar:IncludeSharedByDefault")] bool? includeShared = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, calendar, _) = Create(account);
        var query = BuildQuery(calendarUrl, calendars, includeShared);
        var items = await calendar.GetAppointmentsForDayAsync(date, query, userId: null, cancellationToken);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerTool(Name = "get_appointments_for_week")]
    [Description("Gets calendar appointments for a week. Default: all discovered calendars. Each appointment includes calendarName/calendarId.")]
    public async Task<string> GetAppointmentsForWeekAsync(
        [Description("Week start yyyy-MM-dd or 'today'")] string startDate = "today",
        [Description("Optional single calendar URL override")] string? calendarUrl = null,
        [Description("Optional calendar filters: display names or collection ids, comma-separated")] string? calendars = null,
        [Description("When set, overrides Calendar:IncludeSharedByDefault")] bool? includeShared = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, calendar, _) = Create(account);
        var query = BuildQuery(calendarUrl, calendars, includeShared);
        var items = await calendar.GetAppointmentsForWeekAsync(startDate, query, userId: null, cancellationToken);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerTool(Name = "add_appointment")]
    [Description(
        "Adds an appointment via CalDAV PUT. Requires calendar (name/id), DefaultWriteCalendar, or calendarUrl when multiple calendars exist. Never writes to all calendars. " +
        "When allDay=true, start/end must be yyyy-MM-dd (no times); end is exclusive (iCal DTEND;VALUE=DATE).")]
    public Task<string> AddAppointmentAsync(
        [Description("Event title")] string summary,
        [Description("Start ISO 8601 or yyyy-MM-dd HH:mm (or yyyy-MM-dd when allDay)")] string start,
        [Description("End, same format as start (exclusive date when allDay)")] string end,
        [Description("Optional location")] string? location = null,
        [Description("Optional description")] string? description = null,
        [Description("Optional IANA timezone (ignored when allDay)")] string? timeZoneId = null,
        [Description("When true, write DTSTART/DTEND VALUE=DATE; start/end are date-only; end exclusive")] bool allDay = false,
        [Description("Target calendar display name or collection id (required when multiple calendars and no DefaultWriteCalendar)")] string? calendar = null,
        [Description("Optional calendar URL override")] string? calendarUrl = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, cal, _) = Create(account);
        DateTime startDt;
        DateTime endDt;
        if (allDay)
        {
            startDt = CalDavCalendarService.ParseAllDayDate(start, nameof(start));
            endDt = CalDavCalendarService.ParseAllDayDate(end, nameof(end));
        }
        else
        {
            startDt = CalDavCalendarService.ParseDateTime(start, nameof(start));
            endDt = CalDavCalendarService.ParseDateTime(end, nameof(end));
        }

        return cal.AddAppointmentAsync(new CalendarAddRequest
        {
            Summary = summary,
            Start = startDt,
            End = endDt,
            Location = location,
            Description = description,
            TimeZoneId = allDay ? null : timeZoneId,
            AllDay = allDay,
            CalendarUrl = calendarUrl,
            CalendarSelector = calendar
        }, userId: null, cancellationToken);
    }

    [McpServerTool(Name = "update_appointment")]
    [Description(
        "Partial-update an appointment by uid + calendar (CalDAV PUT with If-Match/ETag; bumps SEQUENCE). " +
        "Only provided fields change. Pass recurrenceId (occurrence start from list) to update one instance; omit for whole series. " +
        "Pass allDay=true with date-only start/end (end exclusive) to switch to VALUE=DATE.")]
    public Task<string> UpdateAppointmentAsync(
        [Description("iCalendar UID from get_appointments_*")] string uid,
        [Description("Target calendar display name or collection id")] string? calendar = null,
        [Description("Optional calendar URL override")] string? calendarUrl = null,
        [Description("New title (omit to leave unchanged)")] string? summary = null,
        [Description("New start ISO 8601 or yyyy-MM-dd when allDay (omit to leave unchanged)")] string? start = null,
        [Description("New end ISO 8601 or yyyy-MM-dd when allDay — exclusive (omit to leave unchanged)")] string? end = null,
        [Description("New location (omit to leave unchanged)")] string? location = null,
        [Description("New description (omit to leave unchanged)")] string? description = null,
        [Description("Optional IANA timezone")] string? timeZoneId = null,
        [Description("When true, VALUE=DATE; when false, timed; omit to leave mode unchanged")] bool? allDay = null,
        [Description("Occurrence start ISO 8601 for single-instance update; omit for whole series")] string? recurrenceId = null,
        [Description("Optional ETag (fetched when omitted)")] string? etag = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, cal, _) = Create(account);
        DateTime? startDt = null;
        DateTime? endDt = null;
        if (!string.IsNullOrWhiteSpace(start))
        {
            startDt = allDay == true
                ? CalDavCalendarService.ParseAllDayDate(start!, nameof(start))
                : CalDavCalendarService.ParseDateTime(start!, nameof(start));
        }

        if (!string.IsNullOrWhiteSpace(end))
        {
            endDt = allDay == true
                ? CalDavCalendarService.ParseAllDayDate(end!, nameof(end))
                : CalDavCalendarService.ParseDateTime(end!, nameof(end));
        }

        return cal.UpdateAppointmentAsync(new CalendarUpdateRequest
        {
            Uid = uid,
            CalendarSelector = calendar,
            CalendarUrl = calendarUrl,
            Summary = summary,
            Start = startDt,
            End = endDt,
            Location = location,
            Description = description,
            TimeZoneId = timeZoneId,
            AllDay = allDay,
            RecurrenceId = string.IsNullOrWhiteSpace(recurrenceId)
                ? null
                : CalDavCalendarService.ParseDateTime(recurrenceId!, nameof(recurrenceId)),
            ETag = etag
        }, userId: null, cancellationToken);
    }

    [McpServerTool(Name = "delete_appointment")]
    [Description(
        "Delete an appointment by uid + calendar. Omit recurrenceId to delete the whole series; " +
        "pass recurrenceId to remove one occurrence (EXDATE / cancel override). Uses If-Match/ETag.")]
    public Task<string> DeleteAppointmentAsync(
        [Description("iCalendar UID from get_appointments_*")] string uid,
        [Description("Target calendar display name or collection id")] string? calendar = null,
        [Description("Optional calendar URL override")] string? calendarUrl = null,
        [Description("Occurrence start ISO 8601 for single-instance delete; omit for whole series")] string? recurrenceId = null,
        [Description("Optional ETag (fetched when omitted)")] string? etag = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, cal, _) = Create(account);
        return cal.DeleteAppointmentAsync(new CalendarDeleteRequest
        {
            Uid = uid,
            CalendarSelector = calendar,
            CalendarUrl = calendarUrl,
            RecurrenceId = string.IsNullOrWhiteSpace(recurrenceId)
                ? null
                : CalDavCalendarService.ParseDateTime(recurrenceId!, nameof(recurrenceId)),
            ETag = etag
        }, userId: null, cancellationToken);
    }

    private (IEmailService Email, ICalendarService Calendar, ResolvedAccount Account) Create(string? account, string? mailboxOverride = null)
    {
        var resolved = catalog.Resolve(account, configuration);
        if (!string.IsNullOrWhiteSpace(mailboxOverride))
            resolved.Imap.Mailbox = mailboxOverride;
        return clientFactory.Create(resolved);
    }

    private static CalendarQueryOptions BuildQuery(string? calendarUrl, string? calendars, bool? includeShared)
    {
        IReadOnlyList<string>? filters = null;
        if (!string.IsNullOrWhiteSpace(calendars))
        {
            filters = calendars
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)
                .ToArray();
            if (filters.Count == 0)
                filters = null;
        }

        return new CalendarQueryOptions
        {
            CalendarUrl = calendarUrl,
            CalendarFilters = filters,
            IncludeShared = includeShared
        };
    }
}
