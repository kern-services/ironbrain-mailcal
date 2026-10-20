namespace Ironbrain.Email;

/// <summary>Thrown when an IMAP UID is not present in the requested mailbox.</summary>
public sealed class EmailNotFoundException : InvalidOperationException
{
    public EmailNotFoundException(string id, string mailbox)
        : base($"Email with UID '{id}' was not found in mailbox '{mailbox}'.")
    {
        EmailId = id;
        Mailbox = mailbox;
    }

    public string EmailId { get; }
    public string Mailbox { get; }
}
