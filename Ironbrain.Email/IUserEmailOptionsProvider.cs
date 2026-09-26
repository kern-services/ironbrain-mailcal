namespace Ironbrain.Email;

/// <summary>
/// Resolves SMTP/IMAP options for a user (e.g. from user_configurations in the database).
/// Implemented by the host (e.g. API); when not registered or returning null, plugins fall back to appsettings.
/// </summary>
public interface IUserEmailOptionsProvider
{
    Task<(SmtpOptions? Smtp, ImapOptions? Imap)> GetOptionsAsync(string? userId, CancellationToken cancellationToken = default);
}
