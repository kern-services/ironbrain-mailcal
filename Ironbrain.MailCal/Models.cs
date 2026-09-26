using Ironbrain.Calendar;
using Ironbrain.Email;

namespace Ironbrain.MailCal;

/// <summary>Resolved mail + optional calendar settings for one named account.</summary>
public sealed class ResolvedAccount
{
    public required string Name { get; init; }
    public required SmtpOptions Smtp { get; init; }
    public required ImapOptions Imap { get; init; }
    public required CalendarOptions Calendar { get; init; }
    public bool IsLegacyShape { get; init; }

    /// <summary>
    /// Throws if <see cref="SmtpOptions.Enabled"/> is false for this account.
    /// Does not fall back to another account.
    /// </summary>
    public void EnsureSendAllowed()
    {
        if (Smtp.Enabled)
            return;

        throw new InvalidOperationException(
            $"SMTP send is disabled for account '{Name}' (Email:Smtp:Enabled=false). "
            + "Use an account with sending enabled (e.g. the bot/assistant mailbox); "
            + "MailCal will not fall back to another account.");
    }
}

/// <summary>Redacted summary for <c>account list</c> / MCP listing.</summary>
public sealed class AccountSummary
{
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool SmtpSendEnabled { get; init; }
    public string? ImapHost { get; init; }
    public string? ImapUsername { get; init; }
    public string? SmtpHost { get; init; }
    public string? SmtpFromAddress { get; init; }
    public string? CalendarSourceUrl { get; init; }
    public bool HasCalendar { get; init; }
}
