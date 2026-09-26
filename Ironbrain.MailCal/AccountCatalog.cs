using Ironbrain.Calendar;
using Ironbrain.Email;
using Microsoft.Extensions.Configuration;

namespace Ironbrain.MailCal;

/// <summary>
/// Loads named mail/calendar accounts from configuration.
/// Supports multi-account <c>accounts</c> map and legacy single-account root <c>Email</c>/<c>Calendar</c>.
/// </summary>
public sealed class AccountCatalog
{
    public const string LegacyAccountName = "default";
    public const string AccountEnvVar = "IRONBRAIN_MAILCAL_ACCOUNT";

    private readonly IReadOnlyDictionary<string, ResolvedAccount> _accounts;
    private readonly string? _configuredDefault;

    private AccountCatalog(IReadOnlyDictionary<string, ResolvedAccount> accounts, string? configuredDefault)
    {
        _accounts = accounts;
        _configuredDefault = configuredDefault;
    }

    public bool IsLegacyShape { get; private init; }

    public IReadOnlyCollection<string> AccountNames => _accounts.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public string? ConfiguredDefaultAccount => _configuredDefault;

    /// <summary>
    /// Build from an <see cref="IConfiguration"/> that already includes JSON file + environment variables.
    /// Env vars like <c>Email__Imap__Password</c> apply to the selected account when that account's
    /// section is bound (legacy: root Email/Calendar; multi: after merging selected account onto a view —
    /// root <c>Email__*</c>/<c>Calendar__*</c> are applied when resolving via <see cref="Resolve"/> overlay).
    /// </summary>
    public static AccountCatalog FromConfiguration(IConfiguration configuration)
    {
        var accountsSection = configuration.GetSection("Accounts");
        var hasAccounts = accountsSection.GetChildren().Any();

        if (hasAccounts)
        {
            var map = new Dictionary<string, ResolvedAccount>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in accountsSection.GetChildren())
            {
                var name = child.Key;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                map[name] = BindAccount(name, child, isLegacy: false);
            }

            if (map.Count == 0)
                throw new InvalidOperationException("Config has an empty 'accounts' object. Add at least one named account.");

            var defaultName = configuration["defaultAccount"] ?? configuration["DefaultAccount"];
            return new AccountCatalog(map, string.IsNullOrWhiteSpace(defaultName) ? null : defaultName.Trim())
            {
                IsLegacyShape = false
            };
        }

        // Legacy: root Email + Calendar → single implicit account "default"
        var hasEmail = configuration.GetSection("Email").GetChildren().Any();
        var hasCalendar = configuration.GetSection("Calendar").GetChildren().Any();
        if (!hasEmail && !hasCalendar)
            throw new InvalidOperationException(
                "No mail/calendar accounts found. Use multi-account 'accounts' or legacy root 'Email'/'Calendar'.");

        var legacy = BindAccount(LegacyAccountName, configuration, isLegacy: true);
        return new AccountCatalog(
            new Dictionary<string, ResolvedAccount>(StringComparer.OrdinalIgnoreCase)
            {
                [LegacyAccountName] = legacy
            },
            LegacyAccountName)
        {
            IsLegacyShape = true
        };
    }

    public IReadOnlyList<AccountSummary> ListSummaries()
    {
        var defaultName = PeekDefaultName();
        return AccountNames.Select(name =>
        {
            var a = _accounts[name];
            var calUrl = a.Calendar.SourceUrl?.Trim();
            var homeUrl = a.Calendar.HomeUrl?.Trim();
            var hasNamedCals = a.Calendar.Calendars is { Count: > 0 };
            return new AccountSummary
            {
                Name = name,
                IsDefault = string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                SmtpSendEnabled = a.Smtp.Enabled,
                ImapHost = NullIfEmpty(a.Imap.Host),
                ImapUsername = NullIfEmpty(a.Imap.Username),
                SmtpHost = NullIfEmpty(a.Smtp.Host),
                SmtpFromAddress = NullIfEmpty(a.Smtp.FromAddress),
                CalendarSourceUrl = NullIfEmpty(calUrl),
                HasCalendar = !string.IsNullOrWhiteSpace(calUrl)
                    || !string.IsNullOrWhiteSpace(homeUrl)
                    || hasNamedCals
            };
        }).ToList();
    }

    public ResolvedAccount Get(string name)
    {
        if (!_accounts.TryGetValue(name, out var account))
        {
            var known = string.Join(", ", AccountNames);
            throw new InvalidOperationException(
                $"Unknown account '{name}'. Known accounts: {known}.");
        }

        return account;
    }

    /// <summary>
    /// Resolve which account to use.
    /// Order: explicit <paramref name="accountName"/> → <c>IRONBRAIN_MAILCAL_ACCOUNT</c> →
    /// <c>defaultAccount</c> → sole account → error if ambiguous.
    /// </summary>
    public ResolvedAccount Resolve(string? accountName, IConfiguration? rootConfigurationForEnvOverlay = null)
    {
        var chosen = ChooseName(accountName);
        var account = Get(chosen);

        if (rootConfigurationForEnvOverlay is null || IsLegacyShape)
            return CloneAccount(account);

        // Overlay root Email__* / Calendar__* env (and IRONBRAIN_ stripped equivalents already in config)
        // onto the selected multi-account so secrets can live in env without choosing an account key.
        return OverlayRootEmailCalendar(account, rootConfigurationForEnvOverlay);
    }

    public string ChooseName(string? accountName)
    {
        if (!string.IsNullOrWhiteSpace(accountName))
            return accountName.Trim();

        var fromEnv = Environment.GetEnvironmentVariable(AccountEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        return PeekDefaultName()
            ?? throw new InvalidOperationException(
                "Multiple accounts are configured but none was selected. Pass --account <name>, set "
                + $"{AccountEnvVar}, or set defaultAccount in the config. Known: {string.Join(", ", AccountNames)}.");
    }

    private string? PeekDefaultName()
    {
        if (!string.IsNullOrWhiteSpace(_configuredDefault))
        {
            if (!_accounts.ContainsKey(_configuredDefault))
                throw new InvalidOperationException(
                    $"defaultAccount '{_configuredDefault}' does not match any entry in accounts. Known: {string.Join(", ", AccountNames)}.");
            return _configuredDefault;
        }

        if (_accounts.Count == 1)
            return _accounts.Keys.First();

        return null;
    }

    private static ResolvedAccount BindAccount(string name, IConfiguration section, bool isLegacy)
    {
        var smtp = new SmtpOptions();
        var imap = new ImapOptions();
        var calendar = new CalendarOptions();
        section.GetSection("Email:Smtp").Bind(smtp);
        section.GetSection("Email:Imap").Bind(imap);
        section.GetSection("Calendar").Bind(calendar);
        return new ResolvedAccount
        {
            Name = name,
            Smtp = smtp,
            Imap = imap,
            Calendar = calendar,
            IsLegacyShape = isLegacy
        };
    }

    private static ResolvedAccount OverlayRootEmailCalendar(ResolvedAccount account, IConfiguration root)
    {
        var smtp = CloneSmtp(account.Smtp);
        var imap = CloneImap(account.Imap);
        var calendar = CloneCalendar(account.Calendar);
        root.GetSection("Email:Smtp").Bind(smtp);
        root.GetSection("Email:Imap").Bind(imap);
        root.GetSection("Calendar").Bind(calendar);
        return new ResolvedAccount
        {
            Name = account.Name,
            Smtp = smtp,
            Imap = imap,
            Calendar = calendar,
            IsLegacyShape = false
        };
    }

    private static ResolvedAccount CloneAccount(ResolvedAccount account) => new()
    {
        Name = account.Name,
        Smtp = CloneSmtp(account.Smtp),
        Imap = CloneImap(account.Imap),
        Calendar = CloneCalendar(account.Calendar),
        IsLegacyShape = account.IsLegacyShape
    };

    private static SmtpOptions CloneSmtp(SmtpOptions s) => new()
    {
        Enabled = s.Enabled,
        Host = s.Host,
        Port = s.Port,
        UseStartTls = s.UseStartTls,
        UseSsl = s.UseSsl,
        Username = s.Username,
        Password = s.Password,
        FromAddress = s.FromAddress,
        FromName = s.FromName
    };

    private static ImapOptions CloneImap(ImapOptions i) => new()
    {
        Host = i.Host,
        Port = i.Port,
        UseSsl = i.UseSsl,
        Username = i.Username,
        Password = i.Password,
        Mailbox = i.Mailbox,
        SentFolder = i.SentFolder
    };

    private static CalendarOptions CloneCalendar(CalendarOptions c) => new()
    {
        SourceUrl = c.SourceUrl,
        HomeUrl = c.HomeUrl,
        Username = c.Username,
        Password = c.Password,
        PreferIcsFeed = c.PreferIcsFeed,
        DefaultWriteCalendar = c.DefaultWriteCalendar,
        IncludeSharedByDefault = c.IncludeSharedByDefault,
        Calendars = c.Calendars?
            .Select(e => new CalendarConfigEntry { Name = e.Name, Url = e.Url })
            .ToList() ?? []
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
