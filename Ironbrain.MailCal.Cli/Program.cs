using System.CommandLine;
using Ironbrain.Calendar;
using Ironbrain.Email;
using Ironbrain.MailCal;
using Ironbrain.MailCal.Cli;
using Microsoft.Extensions.DependencyInjection;

var configOption = new Option<string?>("--config")
{
    Description = "Path to mailcal JSON config (default: $IRONBRAIN_MAILCAL_CONFIG, ~/.config/ironbrain/mailcal.json, or ./appsettings.json)",
    Recursive = true
};
var verboseOption = new Option<bool>("--verbose")
{
    Description = "Enable debug logging",
    Recursive = true
};
var accountOption = new Option<string?>("--account", "-a")
{
    Description = "Named account from config (default: $IRONBRAIN_MAILCAL_ACCOUNT, defaultAccount, or the only account)",
    Recursive = true
};

var root = new RootCommand("Ironbrain MailCal — IMAP/SMTP + CalDAV/iCalendar CLI for self-hosted servers (Mailcow, SOGo, etc.)");
root.Options.Add(configOption);
root.Options.Add(verboseOption);
root.Options.Add(accountOption);

// ---- account ----
var accountCmd = new Command("account", "List or inspect configured accounts (passwords never shown)");
root.Subcommands.Add(accountCmd);

var accountList = new Command("list", "List account names with hosts/usernames (no passwords)");
accountList.SetAction(parseResult =>
{
    var (_, catalog, _) = Open(parseResult);
    ConfigLoader.WriteJson(catalog.ListSummaries());
});
accountCmd.Subcommands.Add(accountList);

var accountShowName = new Argument<string>("name") { Description = "Account name" };
var accountShow = new Command("show", "Show one account (redacted — no passwords)");
accountShow.Arguments.Add(accountShowName);
accountShow.SetAction(parseResult =>
{
    var (_, catalog, _) = Open(parseResult);
    var name = parseResult.GetValue(accountShowName)!;
    var account = catalog.Get(name);
    var defaultName = catalog.ListSummaries().FirstOrDefault(s => s.IsDefault)?.Name;
    ConfigLoader.WriteJson(new
    {
        name = account.Name,
        isDefault = string.Equals(account.Name, defaultName, StringComparison.OrdinalIgnoreCase),
        isLegacyShape = account.IsLegacyShape,
        email = new
        {
            imap = new
            {
                host = account.Imap.Host,
                port = account.Imap.Port,
                useSsl = account.Imap.UseSsl,
                username = account.Imap.Username,
                password = Redact(account.Imap.Password),
                mailbox = account.Imap.Mailbox,
                sentFolder = account.Imap.SentFolder,
                archiveFolder = account.Imap.ArchiveFolder
            },
            smtp = new
            {
                host = account.Smtp.Host,
                port = account.Smtp.Port,
                useStartTls = account.Smtp.UseStartTls,
                        useSsl = account.Smtp.UseSsl,
                        enabled = account.Smtp.Enabled,
                        username = account.Smtp.Username,
                password = Redact(account.Smtp.Password),
                fromAddress = account.Smtp.FromAddress,
                fromName = account.Smtp.FromName
            }
        },
        calendar = new
        {
            sourceUrl = account.Calendar.SourceUrl,
            homeUrl = account.Calendar.HomeUrl,
            username = account.Calendar.Username,
            password = Redact(account.Calendar.Password),
            preferIcsFeed = account.Calendar.PreferIcsFeed,
            defaultWriteCalendar = account.Calendar.DefaultWriteCalendar,
            includeSharedByDefault = account.Calendar.IncludeSharedByDefault,
            calendars = account.Calendar.Calendars?.Select(c => new { name = c.Name, url = c.Url }).ToList()
        }
    });
});
accountCmd.Subcommands.Add(accountShow);

// ---- mail ----
var mail = new Command("mail", "IMAP/SMTP mail operations");
root.Subcommands.Add(mail);

var limitOpt = new Option<int>("--limit") { Description = "Max messages", DefaultValueFactory = _ => 20 };
var offsetOpt = new Option<int>("--offset") { Description = "Offset from newest", DefaultValueFactory = _ => 0 };
var mailboxOpt = new Option<string?>("--mailbox") { Description = "Override IMAP mailbox (default from account config)" };

var mailList = new Command("list", "List recent messages from the IMAP mailbox");
mailList.Options.Add(limitOpt);
mailList.Options.Add(offsetOpt);
mailList.Options.Add(mailboxOpt);
mailList.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, _) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt);
        var items = await email.ListInboxAsync(parseResult.GetValue(limitOpt), parseResult.GetValue(offsetOpt), userId: null, ct);
        ConfigLoader.WriteJson(items);
    }
});
mail.Subcommands.Add(mailList);

var idArg = new Argument<string>("id") { Description = "IMAP unique id" };
var mailGet = new Command("get", "Read one message by IMAP unique id");
mailGet.Arguments.Add(idArg);
mailGet.Options.Add(mailboxOpt);
mailGet.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, _) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt);
        var content = await email.GetEmailAsync(parseResult.GetValue(idArg)!, userId: null, ct);
        ConfigLoader.WriteJson(content);
    }
});
mail.Subcommands.Add(mailGet);

var toOpt = new Option<string>("--to") { Description = "Recipient" };
toOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--to is required"); });
var subjectOpt = new Option<string>("--subject") { Description = "Subject" };
subjectOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--subject is required"); });
var bodyOpt = new Option<string?>("--body") { Description = "Plain text body" };
var htmlOpt = new Option<string?>("--html") { Description = "HTML body" };
var bodyFileOpt = new Option<string?>("--body-file") { Description = "Read plain text body from file" };

var mailSend = new Command("send", "Send a message via SMTP (also appends to IMAP Sent when configured)");
mailSend.Options.Add(toOpt);
mailSend.Options.Add(subjectOpt);
mailSend.Options.Add(bodyOpt);
mailSend.Options.Add(htmlOpt);
mailSend.Options.Add(bodyFileOpt);
mailSend.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, account) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt: null);
        account.EnsureSendAllowed();
        var text = parseResult.GetValue(bodyOpt);
        var bodyFile = parseResult.GetValue(bodyFileOpt);
        if (!string.IsNullOrWhiteSpace(bodyFile))
            text = await File.ReadAllTextAsync(bodyFile!, ct);
        await email.SendEmailAsync(new EmailSendRequest
        {
            To = parseResult.GetValue(toOpt)!,
            Subject = parseResult.GetValue(subjectOpt)!,
            TextBody = text,
            HtmlBody = parseResult.GetValue(htmlOpt)
        }, userId: null, ct);
        Console.WriteLine($"Sent (account={account.Name}).");
    }
});
mail.Subcommands.Add(mailSend);

var replyIdArg = new Argument<string>("id") { Description = "IMAP unique id of the message to reply to" };
var replyBodyOpt = new Option<string?>("--body") { Description = "Plain text body" };
var replyBodyFileOpt = new Option<string?>("--body-file") { Description = "Read plain text body from file" };
var mailReply = new Command("reply", "Reply to a message (loads subject/from via IMAP, sends via SMTP)");
mailReply.Arguments.Add(replyIdArg);
mailReply.Options.Add(replyBodyOpt);
mailReply.Options.Add(replyBodyFileOpt);
mailReply.Options.Add(mailboxOpt);
mailReply.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, account) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt);
        account.EnsureSendAllowed();
        var original = await email.GetEmailAsync(parseResult.GetValue(replyIdArg)!, userId: null, ct);
        var to = ExtractEmailAddress(original.From);
        if (string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException($"Could not parse reply address from From: {original.From}");

        var text = parseResult.GetValue(replyBodyOpt);
        var bodyFile = parseResult.GetValue(replyBodyFileOpt);
        if (!string.IsNullOrWhiteSpace(bodyFile))
            text = await File.ReadAllTextAsync(bodyFile!, ct);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Provide --body or --body-file for the reply.");

        var subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? original.Subject
            : "Re: " + original.Subject;

        await email.SendEmailAsync(new EmailSendRequest
        {
            To = to,
            Subject = subject,
            TextBody = text
        }, userId: null, ct);
        Console.WriteLine($"Replied to {to} (account={account.Name}).");
    }
});
mail.Subcommands.Add(mailReply);

var moveUidArg = new Argument<string>("uid") { Description = "IMAP unique id (UID) of the message to move" };
var moveToOpt = new Option<string>("--to") { Description = "Destination IMAP mailbox (e.g. Archive/2026)" };
moveToOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--to is required"); });
var mailMove = new Command("move", "Move a message by IMAP UID (prefers UID MOVE; else COPY+\\Deleted+EXPUNGE)");
mailMove.Arguments.Add(moveUidArg);
mailMove.Options.Add(moveToOpt);
mailMove.Options.Add(mailboxOpt);
mailMove.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, account) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt);
        var result = await email.MoveEmailAsync(
            parseResult.GetValue(moveUidArg)!,
            parseResult.GetValue(moveToOpt)!,
            sourceMailbox: null,
            userId: null,
            ct);
        ConfigLoader.WriteJson(new
        {
            result.Id,
            result.FromMailbox,
            result.ToMailbox,
            result.Method,
            account = account.Name
        });
    }
});
mail.Subcommands.Add(mailMove);

var archiveUidArg = new Argument<string>("uid") { Description = "IMAP unique id (UID) of the message to archive" };
var mailArchive = new Command("archive", "Move a message into Email:Imap:ArchiveFolder (default Archive/{CurrentYear}; {CurrentYear}=UTC now, other tokens=message Date)");
mailArchive.Arguments.Add(archiveUidArg);
mailArchive.Options.Add(mailboxOpt);
mailArchive.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (email, _, account) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt);
        var result = await email.ArchiveEmailAsync(
            parseResult.GetValue(archiveUidArg)!,
            sourceMailbox: null,
            userId: null,
            ct);
        ConfigLoader.WriteJson(new
        {
            result.Id,
            result.FromMailbox,
            result.ToMailbox,
            result.Method,
            account = account.Name
        });
    }
});
mail.Subcommands.Add(mailArchive);

// ---- cal ----
var cal = new Command("cal", "CalDAV / iCalendar operations");
root.Subcommands.Add(cal);

var dateOpt = new Option<string>("--date") { Description = "yyyy-MM-dd or 'today'", DefaultValueFactory = _ => "today" };
var rangeOpt = new Option<string>("--range") { Description = "day or week", DefaultValueFactory = _ => "day" };
var calUrlOpt = new Option<string?>("--url") { Description = "Override a single calendar collection / .ics URL (skips multi-calendar discovery)" };
var calendarOpt = new Option<string[]?>("--calendar", "-c")
{
    Description = "Filter by calendar display name or collection id (repeatable or comma-separated, e.g. Family,personal)",
    AllowMultipleArgumentsPerToken = true
};
var includeSharedOpt = new Option<bool>("--include-shared")
{
    Description = "Include shared/delegated calendars (default: Calendar:IncludeSharedByDefault, usually true)"
};
var excludeSharedOpt = new Option<bool>("--exclude-shared")
{
    Description = "Omit shared/delegated calendars from discovery results"
};

var calCalendars = new Command("calendars", "List CalDAV calendar collections (displayname, href/id, shared; passwords never shown)");
calCalendars.Options.Add(includeSharedOpt);
calCalendars.Options.Add(excludeSharedOpt);
calCalendars.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (_, calendar, _) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt: null);
        var includeShared = ResolveIncludeShared(parseResult, includeSharedOpt, excludeSharedOpt);
        var items = await calendar.ListCalendarsAsync(userId: null, includeShared: includeShared, cancellationToken: ct);
        ConfigLoader.WriteJson(items.Select(c => new
        {
            displayName = c.DisplayName,
            href = c.Href,
            collectionId = c.CollectionId,
            isShared = c.IsShared
        }));
    }
});
cal.Subcommands.Add(calCalendars);

var calList = new Command("list", "List appointments (default: all discovered calendars for the account)");
calList.Options.Add(dateOpt);
calList.Options.Add(rangeOpt);
calList.Options.Add(calUrlOpt);
calList.Options.Add(calendarOpt);
calList.Options.Add(includeSharedOpt);
calList.Options.Add(excludeSharedOpt);
calList.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (_, calendar, _) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt: null);
        var date = parseResult.GetValue(dateOpt)!;
        var range = parseResult.GetValue(rangeOpt)!;
        var query = BuildCalendarQuery(parseResult, calUrlOpt, calendarOpt, includeSharedOpt, excludeSharedOpt);
        IReadOnlyList<CalendarAppointment> items = range.Equals("week", StringComparison.OrdinalIgnoreCase)
            ? await calendar.GetAppointmentsForWeekAsync(date, query, userId: null, ct)
            : await calendar.GetAppointmentsForDayAsync(date, query, userId: null, ct);
        ConfigLoader.WriteJson(items);
    }
});
cal.Subcommands.Add(calList);

var summaryOpt = new Option<string>("--summary") { Description = "Event title" };
summaryOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--summary is required"); });
var startOpt = new Option<string>("--start") { Description = "Start (ISO 8601 or yyyy-MM-dd HH:mm)" };
startOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--start is required"); });
var endOpt = new Option<string>("--end") { Description = "End (same format as --start)" };
endOpt.Validators.Add(r => { if (string.IsNullOrWhiteSpace(r.GetValueOrDefault<string>())) r.AddError("--end is required"); });
var locationOpt = new Option<string?>("--location");
var descOpt = new Option<string?>("--description");
var tzOpt = new Option<string?>("--tz") { Description = "IANA timezone (e.g. Europe/Berlin)" };
var writeCalendarOpt = new Option<string?>("--calendar", "-c")
{
    Description = "Target calendar display name or collection id (required when multiple calendars exist unless DefaultWriteCalendar or --url is set)"
};

var calAdd = new Command("add", "Add an appointment via CalDAV PUT (never writes to all calendars)");
calAdd.Options.Add(summaryOpt);
calAdd.Options.Add(startOpt);
calAdd.Options.Add(endOpt);
calAdd.Options.Add(locationOpt);
calAdd.Options.Add(descOpt);
calAdd.Options.Add(tzOpt);
calAdd.Options.Add(calUrlOpt);
calAdd.Options.Add(writeCalendarOpt);
calAdd.SetAction(async (parseResult, ct) =>
{
    var (sp, catalog, configuration) = Open(parseResult);
    await using (sp)
    {
        var (_, calendar, account) = CreateClients(sp, catalog, configuration, parseResult, mailboxOpt: null);
        var result = await calendar.AddAppointmentAsync(new CalendarAddRequest
        {
            Summary = parseResult.GetValue(summaryOpt)!,
            Start = CalDavCalendarService.ParseDateTime(parseResult.GetValue(startOpt)!, "start"),
            End = CalDavCalendarService.ParseDateTime(parseResult.GetValue(endOpt)!, "end"),
            Location = parseResult.GetValue(locationOpt),
            Description = parseResult.GetValue(descOpt),
            TimeZoneId = parseResult.GetValue(tzOpt),
            CalendarUrl = parseResult.GetValue(calUrlOpt),
            CalendarSelector = parseResult.GetValue(writeCalendarOpt)
        }, userId: null, ct);
        Console.WriteLine($"{result} (account={account.Name})");
    }
});
cal.Subcommands.Add(calAdd);

var configShow = new Command("config", "Configuration helpers");
var configPathCmd = new Command("path", "Print the config file that would be used");
configPathCmd.SetAction(parseResult =>
{
    var path = ConfigLoader.ResolveConfigPath(parseResult.GetValue(configOption));
    Console.WriteLine(path ?? "(none found — pass --config or set IRONBRAIN_MAILCAL_CONFIG)");
});
configShow.Subcommands.Add(configPathCmd);
root.Subcommands.Add(configShow);

return await root.Parse(args).InvokeAsync();

(ServiceProvider Sp, AccountCatalog Catalog, Microsoft.Extensions.Configuration.IConfiguration Configuration) Open(ParseResult parseResult)
{
    var configPath = parseResult.GetValue(configOption);
    var verbose = parseResult.GetValue(verboseOption);
    var configuration = ConfigLoader.BuildConfiguration(configPath);
    return ConfigLoader.BuildServices(configuration, verbose);
}

(IEmailService Email, ICalendarService Calendar, ResolvedAccount Account) CreateClients(
    ServiceProvider sp,
    AccountCatalog catalog,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    ParseResult parseResult,
    Option<string?>? mailboxOpt)
{
    var accountName = parseResult.GetValue(accountOption);
    var resolved = catalog.Resolve(accountName, configuration);
    if (mailboxOpt is not null)
    {
        var mailbox = parseResult.GetValue(mailboxOpt);
        if (!string.IsNullOrWhiteSpace(mailbox))
            resolved.Imap.Mailbox = mailbox;
    }

    var factory = sp.GetRequiredService<MailCalClientFactory>();
    return factory.Create(resolved);
}

static string Redact(string? secret) =>
    string.IsNullOrEmpty(secret) ? "" : "***";

static string ExtractEmailAddress(string from)
{
    var start = from.LastIndexOf('<');
    var end = from.LastIndexOf('>');
    if (start >= 0 && end > start)
        return from[(start + 1)..end].Trim();
    return from.Trim();
}

static bool? ResolveIncludeShared(ParseResult parseResult, Option<bool> includeSharedOpt, Option<bool> excludeSharedOpt)
{
    var include = parseResult.GetValue(includeSharedOpt);
    var exclude = parseResult.GetValue(excludeSharedOpt);
    if (include && exclude)
        throw new InvalidOperationException("Use only one of --include-shared or --exclude-shared.");
    if (include)
        return true;
    if (exclude)
        return false;
    return null; // use Calendar:IncludeSharedByDefault
}

static CalendarQueryOptions BuildCalendarQuery(
    ParseResult parseResult,
    Option<string?> calUrlOpt,
    Option<string[]?> calendarOpt,
    Option<bool> includeSharedOpt,
    Option<bool> excludeSharedOpt)
{
    var filters = parseResult.GetValue(calendarOpt);
    var flat = filters?
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(t => t.Length > 0)
        .ToArray();

    return new CalendarQueryOptions
    {
        CalendarUrl = parseResult.GetValue(calUrlOpt),
        CalendarFilters = flat is { Length: > 0 } ? flat : null,
        IncludeShared = ResolveIncludeShared(parseResult, includeSharedOpt, excludeSharedOpt)
    };
}
