namespace Ironbrain.Email;

public interface IEmailService
{
    /// <param name="userId">When set, uses this user's mail config from the host; otherwise uses appsettings / file config.</param>
    /// <param name="mailbox">Optional IMAP folder override (e.g. CLI <c>--mailbox</c>). When null, uses the configured default mailbox.</param>
    /// <param name="accountId">Optional host account id. When null, the host uses the first mail config.</param>
    Task<IReadOnlyList<EmailSummary>> ListInboxAsync(
        int limit = 20,
        int offset = 0,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? mailbox = null,
        string? accountId = null);

    /// <param name="mailbox">Optional IMAP folder override. When null, uses the configured default mailbox.</param>
    /// <param name="accountId">Optional host account id; null → first mail config.</param>
    Task<EmailContent> GetEmailAsync(
        string id,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? mailbox = null,
        string? accountId = null);

    /// <param name="accountId">Optional host account id; null → first mail config.</param>
    Task SendEmailAsync(
        EmailSendRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null);

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
        CancellationToken cancellationToken = default,
        string? accountId = null);

    /// <summary>
    /// Moves a message by IMAP UID into <see cref="ImapOptions.ArchiveFolder"/>
    /// (default <c>Archive/{CurrentYear}</c> when unset/empty).
    /// <c>{CurrentYear}</c> expands from UTC now; other date tokens from
    /// <paramref name="messageDate"/> when provided, else message Date, else UTC now.
    /// </summary>
    Task<EmailMoveResult> ArchiveEmailAsync(
        string id,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? messageDate = null,
        string? accountId = null);

    /// <summary>
    /// Moves the message to the account trash folder (<see cref="ImapOptions.TrashFolder"/>, default <c>Trash</c>).
    /// </summary>
    Task<EmailMoveResult> TrashEmailAsync(
        string id,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null);
}
