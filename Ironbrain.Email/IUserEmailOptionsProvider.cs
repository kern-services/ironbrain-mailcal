namespace Ironbrain.Email;

/// <summary>
/// Resolves SMTP/IMAP options for a user (e.g. from a host database).
/// Implemented by the host (e.g. API); when not registered or returning null, services fall back to appsettings / file config.
/// </summary>
public interface IUserEmailOptionsProvider
{
    /// <param name="accountId">Optional host mail account id. When null, uses the first mail config.</param>
    Task<(SmtpOptions? Smtp, ImapOptions? Imap)> GetOptionsAsync(
        string? userId,
        CancellationToken cancellationToken = default,
        string? accountId = null);
}
