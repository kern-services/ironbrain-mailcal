namespace Ironbrain.Email;

public interface IEmailService
{
    /// <param name="userId">When set, uses this user's mail config from the database; otherwise uses appsettings.</param>
    Task<IReadOnlyList<EmailSummary>> ListInboxAsync(int limit = 20, int offset = 0, string? userId = null, CancellationToken cancellationToken = default);

    Task<EmailContent> GetEmailAsync(string id, string? userId = null, CancellationToken cancellationToken = default);

    Task SendEmailAsync(EmailSendRequest request, string? userId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a message by IMAP UID from the account mailbox (or <paramref name="sourceMailbox"/>) to <paramref name="destinationMailbox"/>.
    /// Prefers UID MOVE; falls back to COPY + \Deleted + EXPUNGE when MOVE is unsupported.
    /// Creates the destination mailbox hierarchy when the server allows.
    /// </summary>
    Task<EmailMoveResult> MoveEmailAsync(
        string id,
        string destinationMailbox,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a message by IMAP UID into the configured <see cref="ImapOptions.ArchiveFolder"/>
    /// (placeholders expanded from the message Date, else UTC now).
    /// Fails if ArchiveFolder is not configured.
    /// </summary>
    Task<EmailMoveResult> ArchiveEmailAsync(
        string id,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default);
}
