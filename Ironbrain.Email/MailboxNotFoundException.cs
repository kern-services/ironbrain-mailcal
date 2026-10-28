namespace Ironbrain.Email;

/// <summary>Thrown when an IMAP mailbox/folder path does not exist (after separator and case-insensitive fallback).</summary>
public sealed class MailboxNotFoundException : InvalidOperationException
{
    public MailboxNotFoundException(string mailbox)
        : base($"Mailbox '{mailbox}' was not found.")
    {
        Mailbox = mailbox;
    }

    public string Mailbox { get; }
}
