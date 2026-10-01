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
    [Description("Lists recent emails from the selected account's IMAP mailbox.")]
    public async Task<string> ListEmailsAsync(
        [Description("Optional account name; defaultAccount / IRONBRAIN_MAILCAL_ACCOUNT / sole account if omitted")] string? account = null,
        [Description("Max number of emails")] int limit = 20,
        [Description("Offset from the most recent")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var (email, _, _) = Create(account);
        var items = await email.ListInboxAsync(limit, offset, userId: null, cancellationToken);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerTool(Name = "get_email")]
    [Description("Gets a specific email by IMAP unique id from the selected account.")]
    public async Task<string> GetEmailAsync(
        [Description("IMAP unique id")] string id,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, _) = Create(account);
        var content = await email.GetEmailAsync(id, userId: null, cancellationToken);
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
    [Description("Moves a message by IMAP UID to a destination mailbox. Prefers UID MOVE; falls back to COPY+\\Deleted+EXPUNGE. Creates the destination folder when the server allows.")]
    public async Task<string> MoveEmailAsync(
        [Description("IMAP unique id (UID)")] string uid,
        [Description("Destination IMAP mailbox (e.g. Archive/2026)")] string toMailbox,
        [Description("Optional source mailbox override (default: account Imap.Mailbox / INBOX)")] string? mailbox = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, resolved) = Create(account, mailbox);
        var result = await email.MoveEmailAsync(uid, toMailbox, sourceMailbox: null, userId: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            result.Id,
            result.FromMailbox,
            result.ToMailbox,
            result.Method,
            account = resolved.Name
        }, JsonOptions);
    }

    [McpServerTool(Name = "archive_email")]
    [Description("Archives a message by IMAP UID into Email:Imap:ArchiveFolder (default Archive/{CurrentYear} when unset). {CurrentYear} expands from UTC now; {YYYY}/{MM}/{YY}/{DD} from message Date, else UTC now.")]
    public async Task<string> ArchiveEmailAsync(
        [Description("IMAP unique id (UID)")] string uid,
        [Description("Optional source mailbox override (default: account Imap.Mailbox / INBOX)")] string? mailbox = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (email, _, resolved) = Create(account, mailbox);
        var result = await email.ArchiveEmailAsync(uid, sourceMailbox: null, userId: null, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            result.Id,
            result.FromMailbox,
            result.ToMailbox,
            result.Method,
            account = resolved.Name
        }, JsonOptions);
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
    [Description("Adds an appointment via CalDAV PUT. Requires calendar (name/id), DefaultWriteCalendar, or calendarUrl when multiple calendars exist. Never writes to all calendars.")]
    public Task<string> AddAppointmentAsync(
        [Description("Event title")] string summary,
        [Description("Start ISO 8601 or yyyy-MM-dd HH:mm")] string start,
        [Description("End, same format as start")] string end,
        [Description("Optional location")] string? location = null,
        [Description("Optional description")] string? description = null,
        [Description("Optional IANA timezone")] string? timeZoneId = null,
        [Description("Target calendar display name or collection id (required when multiple calendars and no DefaultWriteCalendar)")] string? calendar = null,
        [Description("Optional calendar URL override")] string? calendarUrl = null,
        [Description("Optional account name")] string? account = null,
        CancellationToken cancellationToken = default)
    {
        var (_, cal, _) = Create(account);
        return cal.AddAppointmentAsync(new CalendarAddRequest
        {
            Summary = summary,
            Start = CalDavCalendarService.ParseDateTime(start, nameof(start)),
            End = CalDavCalendarService.ParseDateTime(end, nameof(end)),
            Location = location,
            Description = description,
            TimeZoneId = timeZoneId,
            CalendarUrl = calendarUrl,
            CalendarSelector = calendar
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
