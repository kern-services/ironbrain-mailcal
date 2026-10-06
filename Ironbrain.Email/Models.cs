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


