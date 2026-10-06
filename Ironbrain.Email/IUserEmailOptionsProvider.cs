namespace Ironbrain.Email;

/// <summary>
/// Resolves SMTP/IMAP options for a user (e.g. from a host database).
/// Implemented by the host (e.g. API); when not registered or returning null, services fall back to appsettings / file config.
/// Either <see cref="SmtpOptions"/> or <see cref="ImapOptions"/> (or both) may be non-null — hosts may return
/// SMTP-only or IMAP-only accounts. Callers that need only one side should tolerate a null on the other.
/// </summary>
public interface IUserEmailOptionsProvider
{
    /// <param name="accountId">
    /// Optional host mail account id. When null, the host should resolve defaults independently:
    /// first IMAP-capable account for IMAP, first SMTP-capable account for SMTP (may differ in multi-account orgs).
    /// </param>
    Task<(SmtpOptions? Smtp, ImapOptions? Imap)> GetOptionsAsync(
        string? userId,
        CancellationToken cancellationToken = default,
        string? accountId = null);
}
