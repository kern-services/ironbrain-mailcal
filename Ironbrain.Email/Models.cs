namespace Ironbrain.Email;

public sealed class EmailSummary
{
    public required string Id { get; set; }
    public required string Subject { get; set; }
    public required string From { get; set; }
    public DateTimeOffset Date { get; set; }
    public bool IsSeen { get; set; }
}

/// <summary>Attachment metadata exposed by <see cref="EmailContent"/> (no binary payload).</summary>
public sealed class EmailAttachmentInfo
{
    public required string FileName { get; set; }
    public string? ContentType { get; set; }
    /// <summary>Decoded size in bytes when known; 0 when unavailable without decoding.</summary>
    public long Size { get; set; }
}

public sealed class EmailContent
{
    public required string Id { get; set; }
    public required string Subject { get; set; }
    public required string From { get; set; }
    /// <summary>Comma-separated To addresses from the envelope (empty when none).</summary>
    public string To { get; set; } = string.Empty;
    /// <summary>Comma-separated Cc addresses from the envelope (empty when none).</summary>
    public string Cc { get; set; } = string.Empty;
    /// <summary>RFC Message-ID header value when present (including angle brackets when the server stored them).</summary>
    public string? MessageId { get; set; }
    public required string BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public DateTimeOffset Date { get; set; }
    /// <summary>Original attachments (file name / type / size). Binary content is not included.</summary>
    public IReadOnlyList<EmailAttachmentInfo> Attachments { get; set; } = [];
}

public sealed class EmailSendRequest
{
    public required string To { get; set; }
    public required string Subject { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
}

/// <summary>
/// Forward an existing IMAP message (including original attachments) to a new recipient via SMTP.
/// Attachments are fetched at send time — not stored in the request.
/// </summary>
public sealed class EmailForwardRequest
{
    /// <summary>IMAP unique id (UID) of the message to forward.</summary>
    public required string EmailId { get; set; }

    /// <summary>Forward recipient.</summary>
    public required string To { get; set; }

    /// <summary>Optional source mailbox/folder (default: account mailbox / INBOX).</summary>
    public string? SourceMailbox { get; set; }

    /// <summary>Optional note/preface placed above the forwarded content.</summary>
    public string? Note { get; set; }

    /// <summary>Optional subject override. When null/empty, uses <c>Fw: {original}</c>.</summary>
    public string? Subject { get; set; }
}

/// <summary>Result of an IMAP UID move or archive.</summary>
public sealed class EmailMoveResult
{
    public required string Id { get; set; }
    public required string FromMailbox { get; set; }
    public required string ToMailbox { get; set; }
    /// <summary><c>MOVE</c> when the server supports IMAP MOVE; otherwise <c>COPY+DELETE</c>.</summary>
    public required string Method { get; set; }
}

/// <summary>Binary attachment for <see cref="EmailSaveDraftRequest"/> (decoded at append time).</summary>
public sealed class EmailDraftAttachment
{
    public required string FileName { get; set; }
    public string? ContentType { get; set; }
    /// <summary>Raw attachment bytes (not base64).</summary>
    public required byte[] Content { get; set; }
}

/// <summary>
/// Save a draft via IMAP APPEND into the account Drafts folder with the <c>\Draft</c> flag.
/// From is always the IMAP account identity (not SMTP send-as).
/// </summary>
public sealed class EmailSaveDraftRequest
{
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Bcc { get; set; }
    public string? Subject { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public IReadOnlyList<EmailDraftAttachment>? Attachments { get; set; }

    /// <summary>Optional IMAP UID of the message being replied to (sets In-Reply-To / References).</summary>
    public string? ReplyToMessageId { get; set; }

    /// <summary>Optional mailbox containing <see cref="ReplyToMessageId"/> (default: account mailbox / INBOX).</summary>
    public string? ReplyToMailbox { get; set; }

    /// <summary>When true and a reply source is loaded, append a quoted original under the body.</summary>
    public bool QuoteOriginal { get; set; }

    /// <summary>Optional Drafts folder override for this call (else account / SPECIAL-USE / name fallback).</summary>
    public string? DraftsFolder { get; set; }
}

/// <summary>Result of IMAP APPEND into Drafts.</summary>
public sealed class EmailSaveDraftResult
{
    /// <summary>IMAP UID of the appended draft when the server returned one; otherwise empty.</summary>
    public required string Id { get; set; }
    public required string Folder { get; set; }
    /// <summary>From address used on the draft (IMAP account identity).</summary>
    public required string From { get; set; }
    public required string Subject { get; set; }
}


