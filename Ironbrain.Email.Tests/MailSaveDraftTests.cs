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
    public async Task BuildDraftMimeAsync_TextReplyToHtmlOriginal_PutsReplyBeforeBlockquote()
    {
        var original = BuildHtmlOriginal(
            """
            <!DOCTYPE html>
            <html><head><meta charset="iso-8859-1"><style>p{color:red}</style></head>
            <body><p>Original HTML body</p></body></html>
            """);

        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Thanks for the update",
            QuoteOriginal = true,
            ReplyToMessageId = "42"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(mime.HtmlBody));
        var html = mime.HtmlBody!;
        var replyIdx = html.IndexOf("Thanks for the update", StringComparison.Ordinal);
        var quoteIdx = html.IndexOf("<blockquote type=\"cite\">", StringComparison.Ordinal);
        Assert.True(replyIdx >= 0, "reply text missing from HtmlBody");
        Assert.True(quoteIdx > replyIdx, "reply must appear before blockquote");
        Assert.Contains("Original HTML body", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<head", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iso-8859-1", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_HtmlOnlyOriginal_TextQuoteViaHtmlToText()
    {
        var original = BuildHtmlOriginal("<html><body><p>Nur HTML Inhalt</p></body></html>");

        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Antwort",
            QuoteOriginal = true,
            ReplyToMessageId = "7"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Contains("Antwort", mime.TextBody, StringComparison.Ordinal);
        Assert.Contains("> ", mime.TextBody, StringComparison.Ordinal);
        Assert.Contains("Nur HTML Inhalt", mime.TextBody, StringComparison.Ordinal);
        // Quote lines must not be blank after the header.
        var afterHeader = mime.TextBody!.Split(["wrote:"], StringSplitOptions.None).Last();
        Assert.Contains("Nur HTML", afterHeader, StringComparison.Ordinal);
        Assert.Contains("> Nur HTML Inhalt", mime.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftMimeAsync_QuoteStripsNestedDocumentChrome()
    {
        var original = BuildHtmlOriginal(
            """
            <!DOCTYPE html PUBLIC "-//W3C//DTD HTML 4.0 Transitional//EN">
            <html><head><meta http-equiv="Content-Type" content="text/html; charset=iso-8859-1">
            <title>x</title></head><body bgcolor="#ffffff"><div>Inner only</div></body></html>
            """);

        var quoted = MailKitEmailService.AppendQuotedOriginalHtml(
            htmlBody: null, textBody: "Hi", original);

        Assert.NotNull(quoted);
        Assert.Contains("<blockquote type=\"cite\">", quoted);
        Assert.Contains("Inner only", quoted);
        Assert.DoesNotContain("<!DOCTYPE", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<head", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("charset=", quoted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_UmlautsUtf8RoundTrip_BothParts()
    {
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("alice@example.com"));
        original.MessageId = "<umlaut@example.com>";
        original.Subject = "Größe";
        original.Date = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        original.Body = new TextPart("plain") { Text = "Schöne Grüße aus Köln" };

        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Danke schön",
            QuoteOriginal = true,
            ReplyToMessageId = "9"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Contains("Danke schön", mime.TextBody, StringComparison.Ordinal);
        Assert.Contains("Schöne Grüße aus Köln", mime.TextBody, StringComparison.Ordinal);
        // HtmlEncode may emit numeric entities; decode before asserting round-trip.
        var htmlDecoded = System.Net.WebUtility.HtmlDecode(mime.HtmlBody);
        Assert.Contains("Danke schön", htmlDecoded, StringComparison.Ordinal);
        Assert.Contains("Schöne Grüße aus Köln", htmlDecoded, StringComparison.Ordinal);
        Assert.Equal("Re: Größe", mime.Subject);
    }

    [Fact]
    public async Task BuildDraftMimeAsync_ExplicitHtmlBodyKept_BeforeQuote()
    {
        var original = BuildHtmlOriginal("<body><p>Orig</p></body>");
        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            HtmlBody = "<p>Explicit reply</p>",
            TextBody = "should not replace html",
            QuoteOriginal = true,
            ReplyToMessageId = "3"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Contains("<p>Explicit reply</p>", mime.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("should not replace html", mime.HtmlBody!, StringComparison.Ordinal);
        var html = mime.HtmlBody!;
        Assert.True(
            html.IndexOf("Explicit reply", StringComparison.Ordinal)
            < html.IndexOf("<blockquote type=\"cite\">", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildDraftMimeAsync_QuoteOriginalFalse_StaysSingleTextPlain()
    {
        var original = BuildHtmlOriginal("<body><p>Should not appear</p></body>");
        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Just a draft",
            QuoteOriginal = false,
            ReplyToMessageId = "1"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Equal("Just a draft", mime.TextBody);
        Assert.True(string.IsNullOrEmpty(mime.HtmlBody));
        Assert.DoesNotContain("Should not appear", mime.TextBody ?? string.Empty, StringComparison.Ordinal);
        // Single text/plain body part (not multipart/alternative).
        Assert.IsType<TextPart>(mime.Body);
        var textPart = (TextPart)mime.Body;
        Assert.True(textPart.IsPlain);
    }

    [Fact]
    public void EmailNotFoundException_CarriesIdAndMailbox_ForHost404Mapping()
    {
        var ex = new EmailNotFoundException("901", "INBOX");
        Assert.Equal("901", ex.EmailId);
        Assert.Equal("INBOX", ex.Mailbox);
        Assert.Contains("901", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void ExtractHtmlBodyInner_PrefersBodyInnerContent()
    {
        var inner = MailKitEmailService.ExtractHtmlBodyInner(
            "<!DOCTYPE html><html><head><meta charset=\"iso-8859-1\"></head><body><p>Hi</p></body></html>");
        Assert.Equal("<p>Hi</p>", inner);
    }

    [Fact]
    public void PlainTextToHtml_EncodesAndBreaksLines()
    {
        var html = MailKitEmailService.PlainTextToHtml("A < B\nC & D");
        Assert.Equal("<div>A &lt; B<br>C &amp; D</div>", html);
    }

    private static MimeMessage BuildHtmlOriginal(string html)
    {
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("alice@example.com"));
        original.MessageId = "<html-orig@example.com>";
        original.Subject = "Status";
        original.Date = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        original.Body = new TextPart("html") { Text = html };
        return original;
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
