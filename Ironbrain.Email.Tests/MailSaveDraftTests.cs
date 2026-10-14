using Ironbrain.Email;
using MimeKit;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailSaveDraftTests
{
    [Fact]
    public void ResolveDraftFromAddress_UsesImapUsername_NotSmtpSendAs()
    {
        var imap = new ImapOptions { Username = "mattanja@kern.services" };
        Assert.Equal("mattanja@kern.services", MailKitEmailService.ResolveDraftFromAddress(imap));
    }

    [Fact]
    public void ResolveDraftFromAddress_ThrowsWhenUsernameMissing()
    {
        var imap = new ImapOptions { Username = "  " };
        Assert.Throws<InvalidOperationException>(() => MailKitEmailService.ResolveDraftFromAddress(imap));
    }

    [Fact]
    public void DraftsFolderNameFallbacks_IncludeDraftsAndEntwuerfe()
    {
        Assert.Equal("Drafts", MailKitEmailService.DraftsFolderNameFallbacks[0]);
        Assert.Contains("Entwürfe", MailKitEmailService.DraftsFolderNameFallbacks);
    }

    [Theory]
    [InlineData(null, "Invoice", "Re: Invoice")]
    [InlineData("", "Invoice", "Re: Invoice")]
    [InlineData("  ", "Invoice", "Re: Invoice")]
    [InlineData("Custom", "Invoice", "Custom")]
    [InlineData(null, "Re: Invoice", "Re: Invoice")]
    [InlineData(null, null, "Re:")]
    [InlineData(null, "", "Re:")]
    public void ResolveReplySubject_AppliesRePrefixOrOverride(
        string? requested,
        string? original,
        string expected)
    {
        Assert.Equal(expected, MailKitEmailService.ResolveReplySubject(requested, original));
    }

    [Fact]
    public void ApplyReplyHeaders_SetsInReplyToAndReferences()
    {
        var original = new MimeMessage();
        original.MessageId = "orig@example.com";
        original.References.Add("thread-root@example.com");
        original.Subject = "Hello";

        var draft = new MimeMessage();
        MailKitEmailService.ApplyReplyHeaders(draft, original);

        // MimeKit normalizes Message-Ids without angle brackets on InReplyTo/References.
        Assert.Equal("orig@example.com", draft.InReplyTo);
        Assert.Contains("thread-root@example.com", draft.References);
        Assert.Contains("orig@example.com", draft.References);
    }

    [Fact]
    public void ApplyReplyHeaders_AcceptsBracketedMessageId()
    {
        var original = new MimeMessage();
        original.Headers[HeaderId.MessageId] = "<bare@example.com>";
        var draft = new MimeMessage();
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        Assert.Equal("bare@example.com", draft.InReplyTo);
        Assert.Contains("bare@example.com", draft.References);
    }

    [Fact]
    public void ApplyReplyHeaders_NoOpWhenOriginalHasNoMessageIdHeader()
    {
        var original = new MimeMessage();
        // Do not touch MessageId (getter may auto-generate); ensure header absent.
        original.Headers.Remove(HeaderId.MessageId);
        var draft = new MimeMessage();
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        Assert.True(string.IsNullOrEmpty(draft.InReplyTo));
        Assert.Empty(draft.References);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_UsesImapFromAndRecipients()
    {
        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            Cc = "bob@example.com",
            Bcc = "secret@example.com",
            Subject = "Draft subject",
            TextBody = "Hello"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", replySource: null, CancellationToken.None);

        Assert.Equal("mattanja@kern.services", mime.From.Mailboxes.First().Address);
        Assert.Equal("alice@example.com", mime.To.Mailboxes.First().Address);
        Assert.Equal("bob@example.com", mime.Cc.Mailboxes.First().Address);
        Assert.Equal("secret@example.com", mime.Bcc.Mailboxes.First().Address);
        Assert.Equal("Draft subject", mime.Subject);
        Assert.Contains("Hello", mime.TextBody);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_ReplyDefaultsSubjectAndHeadersAndQuote()
    {
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("alice@example.com"));
        original.MessageId = "<orig@example.com>";
        original.Subject = "Status";
        original.Date = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        original.Body = new TextPart("plain") { Text = "Original body" };

        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Thanks",
            QuoteOriginal = true,
            ReplyToMessageId = "42"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Equal("Re: Status", mime.Subject);
        Assert.Equal("orig@example.com", mime.InReplyTo);
        Assert.Contains("Thanks", mime.TextBody);
        Assert.Contains("> Original body", mime.TextBody);
        Assert.Contains("alice@example.com", mime.TextBody);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_IncludesAttachments()
    {
        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            Subject = "With file",
            TextBody = "see attached",
            Attachments =
            [
                new EmailDraftAttachment
                {
                    FileName = "note.txt",
                    ContentType = "text/plain",
                    Content = "hi"u8.ToArray()
                }
            ]
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", null, CancellationToken.None);

        Assert.Single(mime.Attachments);
    }

    [Fact]
    public void FormatAddressList_JoinsRecipients()
    {
        var list = new InternetAddressList
        {
            MailboxAddress.Parse("a@example.com"),
            MailboxAddress.Parse("b@example.com")
        };
        Assert.Contains("a@example.com", MailKitEmailService.FormatAddressList(list));
        Assert.Contains("b@example.com", MailKitEmailService.FormatAddressList(list));
        Assert.Equal(string.Empty, MailKitEmailService.FormatAddressList(null));
    }
}
