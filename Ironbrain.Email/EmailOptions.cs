namespace Ironbrain.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Email:Smtp";

    /// <summary>
    /// Default MailKit socket timeout (ms). Keeps bot/outbox confirm under typical reverse-proxy
    /// read timeouts (~60s) when SMTP is unreachable or hangs on TLS/handshake.
    /// </summary>
    public const int DefaultTimeoutMs = 15_000;

    /// <summary>
    /// When false, SMTP send/reply is refused for this account (IMAP read still works).
    /// Defaults to true for backward compatibility.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public bool UseSsl { get; set; } = false;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;

    /// <summary>
    /// MailKit <c>SmtpClient.Timeout</c> in milliseconds (connect / auth / send socket I/O).
    /// Also bounds the overall send CancellationToken. Default <see cref="DefaultTimeoutMs"/>.
    /// </summary>
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;
}

public sealed class ImapOptions
{
    public const string SectionName = "Email:Imap";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Mailbox { get; set; } = "INBOX";
    /// <summary>IMAP folder for sent messages (e.g. "Sent", "Sent Items", "INBOX.Sent"). If empty, sent copies are not saved.</summary>
    public string SentFolder { get; set; } = "Sent";

    /// <summary>
    /// IMAP archive mailbox pattern for <c>mail archive</c> (default <c>Archive/{CurrentYear}</c>).
    /// Unset or empty uses the default so archive works without config.
    /// <c>{CurrentYear}</c> expands from UTC now; <c>{YYYY}</c>/<c>{YY}</c>/<c>{MM}</c>/<c>{DD}</c>
    /// expand from the message Date when available, otherwise UTC now.
    /// </summary>
    public string ArchiveFolder { get; set; } = ArchiveFolderPath.DefaultPattern;

    /// <summary>
    /// IMAP trash folder for <c>mail trash</c> (e.g. <c>Trash</c>, <c>Deleted Items</c>, <c>[Gmail]/Trash</c>).
    /// Default <c>Trash</c>.
    /// </summary>
    public string TrashFolder { get; set; } = "Trash";

    /// <summary>
    /// Optional IMAP Drafts folder override for <see cref="IEmailService.SaveDraftAsync"/>.
    /// When empty, resolution uses SPECIAL-USE <c>\Drafts</c>, then name fallbacks
    /// (<c>Drafts</c>, <c>Entwürfe</c>).
    /// </summary>
    public string DraftsFolder { get; set; } = string.Empty;

    /// <summary>
    /// MailKit <c>ImapClient.Timeout</c> in milliseconds. Default matches
    /// <see cref="SmtpOptions.DefaultTimeoutMs"/> so Sent-folder append cannot hang forever.
    /// </summary>
    public int TimeoutMs { get; set; } = SmtpOptions.DefaultTimeoutMs;
}
