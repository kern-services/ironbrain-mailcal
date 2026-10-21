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

    [Theory]
    [InlineData("<bare@example.com>", "bare@example.com")]
    [InlineData("[bare@example.com]", "[bare@example.com]")]
    [InlineData("<[bare@example.com]>", "[bare@example.com]")]
    [InlineData("bare@example.com", "bare@example.com")]
    public void NormalizeMessageId_StripsOnlyOuterAngleBrackets(string input, string expected)
    {
        Assert.Equal(expected, MailKitEmailService.NormalizeMessageId(input));
    }

    [Theory]
    [InlineData("<[312dd034055449fc9a067c80b86f6f85-JFZGS42DN5WW25LONFRWC5DJN5XFA3DBORTG64TNFVIHE33EFVBEYMSQPRHHKR3FOR6EK3LBNFWFA5LCNRUXG2DFOJ6EK6DPKNWXI4A=@microsoft.com]>")]
    [InlineData("[312dd034055449fc9a067c80b86f6f85-JFZGS42DN5WW25LONFRWC5DJN5XFA3DBORTG64TNFVIHE33EFVBEYMSQPRHHKR3FOR6EK3LBNFWFA5LCNRUXG2DFOJ6EK6DPKNWXI4A=@microsoft.com]")]
    public void ApplyReplyHeaders_ExchangeMessageId_WriteAndReparse_MatchesOriginalMessageId(string rawMessageId)
    {
        // Exchange [id@microsoft.com] *is* the Message-Id (MimeKit MessageId == "[id]").
        // Writing In-Reply-To: <[id]> matches the original for threading — do not strip [].
        var original = LoadMimeWithHeaders(
            $"Message-ID: {rawMessageId}\r\n",
            body: "original");
        Assert.StartsWith("[", original.MessageId);
        Assert.EndsWith("]", original.MessageId);

        var draft = new MimeMessage();
        draft.From.Add(MailboxAddress.Parse("a@example.com"));
        draft.To.Add(MailboxAddress.Parse("b@example.com"));
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        draft.Body = new TextPart("plain") { Text = "reply" };

        Assert.Equal(original.MessageId, draft.InReplyTo);
        Assert.Equal("<" + original.MessageId + ">", draft.Headers[HeaderId.InReplyTo]);

        var parsed = WriteAndReload(draft);
        Assert.Equal(original.MessageId, parsed.InReplyTo);
        Assert.Contains(original.MessageId!, parsed.References);
        Assert.Equal("<" + original.MessageId + ">", parsed.Headers[HeaderId.InReplyTo]);
    }

    [Fact]
    public void ApplyReplyHeaders_PreservesPriorBracketedReferencesByteForByte()
    {
        const string root = "[root-JFZGS42DN5WW25LONFRWC5DJN5XFA3DBORTG64TNFVIHE33EFVBEYMSQPRHHKR3FOR6EK3LBNFWFA5LCNRUXG2DFOJ6EK6DPKNWXI4A=@microsoft.com]";
        const string child = "[child-JFZGS42DN5WW25LONFRWC5DJN5XFA3DBORTG64TNFVIHE33EFVBEYMSQPRHHKR3FOR6EK3LBNFWFA5LCNRUXG2DFOJ6EK6DPKNWXI4A=@microsoft.com]";

        var original = LoadMimeWithHeaders(
            $"Message-ID: {child}\r\nReferences: <{root}>\r\n",
            body: "original");
        var priorExact = original.References.ToArray();
        Assert.Equal(new[] { root }, priorExact);

        var draft = new MimeMessage();
        draft.From.Add(MailboxAddress.Parse("a@example.com"));
        draft.To.Add(MailboxAddress.Parse("b@example.com"));
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        draft.Body = new TextPart("plain") { Text = "reply" };

        // First N-1 entries must match the source References tokens exactly (byte-for-byte).
        Assert.Equal(priorExact, draft.References.Take(priorExact.Length).ToArray());
        Assert.Equal(new[] { root, child }, draft.References.ToArray());
        var parsed = WriteAndReload(draft);
        Assert.Equal(priorExact, parsed.References.Take(priorExact.Length).ToArray());
        Assert.Equal(new[] { root, child }, parsed.References.ToArray());
        Assert.Equal(child, parsed.InReplyTo);
    }

    [Fact]
    public void ApplyReplyHeaders_DoesNotDuplicateMessageId_CaseInsensitive()
    {
        // Prior References keeps mixed case; Message-ID differs only by case.
        // Duplicate check must be OrdinalIgnoreCase so we do not append a second token,
        // while the prior entry stays byte-for-byte (original casing).
        const string priorCased = "AbC@example.com";
        const string messageIdLower = "abc@example.com";

        var original = LoadMimeWithHeaders(
            $"Message-ID: <{messageIdLower}>\r\nReferences: <{priorCased}>\r\n",
            body: "original");
        Assert.Equal(new[] { priorCased }, original.References.ToArray());

        var draft = new MimeMessage();
        draft.From.Add(MailboxAddress.Parse("a@example.com"));
        draft.To.Add(MailboxAddress.Parse("b@example.com"));
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        draft.Body = new TextPart("plain") { Text = "reply" };

        Assert.Equal(messageIdLower, draft.InReplyTo);
        Assert.Equal(new[] { priorCased }, draft.References.ToArray());
        var parsed = WriteAndReload(draft);
        Assert.Equal(new[] { priorCased }, parsed.References.ToArray());
        Assert.Equal(messageIdLower, parsed.InReplyTo);
    }

    [Fact]
    public void ApplyReplyHeaders_LongReferences_FoldsUnder998_KeepsAllIdsInOrder()
    {
        var prior = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            prior.Add(
                $"[{new string('a', 40)}{i:D4}-JFZGS42DN5WW25LONFRWC5DJN5XFA3DBORTG64TNFVIHE33EFVBEYMSQPRHHKR3FOR6EK3LBNFWFA5LCNRUXG2DFOJ6EK6DPKNWXI4A=@microsoft.com]");
        }

        var original = new MimeMessage();
        original.Headers[HeaderId.MessageId] = prior[^1];
        foreach (var id in prior.Take(prior.Count - 1))
            original.References.Add(id);

        var draft = new MimeMessage();
        draft.From.Add(MailboxAddress.Parse("a@example.com"));
        draft.To.Add(MailboxAddress.Parse("b@example.com"));
        MailKitEmailService.ApplyReplyHeaders(draft, original);
        draft.Body = new TextPart("plain") { Text = "reply" };

        Assert.Equal(prior, draft.References.ToArray());

        using var ms = new MemoryStream();
        draft.WriteTo(ms);
        var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var refsIdx = raw.IndexOf("References:", StringComparison.Ordinal);
        Assert.True(refsIdx >= 0);
        var headerEnd = raw.IndexOf("\n\n", refsIdx, StringComparison.Ordinal);
        if (headerEnd < 0)
            headerEnd = raw.IndexOf("\r\n\r\n", refsIdx, StringComparison.Ordinal);
        Assert.True(headerEnd > refsIdx);
        var refsBlock = raw[refsIdx..headerEnd].Replace("\r\n", "\n");
        foreach (var line in refsBlock.Split('\n'))
            Assert.True(line.Length <= 998, $"References fold line length {line.Length} > 998");

        ms.Position = 0;
        var parsed = MimeMessage.Load(ms);
        Assert.Equal(prior, parsed.References.ToArray());
    }

    [Fact]
    public void MimeKit_DropsBareExchangeReferencesWithoutAngleBrackets()
    {
        // Should-fix documentation: raw References: [id] (no <>) is dropped on write.
        // ApplyReplyHeaders uses MessageIdList.Add so MimeKit emits <[id]> instead.
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("a@example.com"));
        message.To.Add(MailboxAddress.Parse("b@example.com"));
        message.Headers[HeaderId.References] =
            "[root@microsoft.com] [child@microsoft.com]";
        message.Body = new TextPart("plain") { Text = "x" };

        Assert.Empty(message.References);
        var parsed = WriteAndReload(message);
        Assert.Empty(parsed.References);
        Assert.True(string.IsNullOrEmpty(parsed.Headers[HeaderId.References]));
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

    static MimeMessage LoadMimeWithHeaders(string extraHeaders, string body)
    {
        var raw =
            "From: a@example.com\r\n" +
            "To: b@example.com\r\n" +
            "Subject: t\r\n" +
            extraHeaders +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "\r\n" +
            body + "\r\n";
        return MimeMessage.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    static MimeMessage WriteAndReload(MimeMessage message)
    {
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        ms.Position = 0;
        return MimeMessage.Load(ms);
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
    public async Task BuildDraftMimeAsync_OutlookStyleHtmlOnly_SkipsHeadChromeInTextQuote()
    {
        // Real-world Outlook HTML: @font-face / MsoNormal in <head>, body styles, list.
        var outlookHtml = """
            <!DOCTYPE html>
            <html xmlns:o="urn:schemas-microsoft-com:office:office"
                  xmlns:w="urn:schemas-microsoft-com:office:word">
            <head>
            <meta http-equiv="Content-Type" content="text/html; charset=utf-8">
            <style>
            @font-face { font-family: "Calibri"; panose-1: 2 15 5 2 2 2 4 3 2 4; }
            p.MsoNormal, li.MsoNormal, div.MsoNormal {
              margin: 0cm; font-size: 11.0pt; font-family: "Calibri",sans-serif;
            }
            </style>
            <title>Meeting notes</title>
            <script>var x = "should not appear";</script>
            </head>
            <body lang="DE" link="#0563C1">
            <div class="MsoNormal">Bitte die <b>Grüße</b> prüfen.</div>
            <ul><li class="MsoNormal">Punkt eins</li><li class="MsoNormal">Punkt zwei</li></ul>
            <hr>
            <style>.MsoChpDefault { font-size: 10pt; }</style>
            <meta name="Generator" content="Microsoft Word">
            <p class="MsoNormal">Schlusszeile</p>
            </body>
            </html>
            """;
        var original = BuildHtmlOriginal(outlookHtml);
        var request = new EmailSaveDraftRequest
        {
            To = "alice@example.com",
            TextBody = "Danke",
            QuoteOriginal = true,
            ReplyToMessageId = "55"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        Assert.Contains("Grüße", mime.TextBody, StringComparison.Ordinal);
        Assert.Contains("Punkt eins", mime.TextBody, StringComparison.Ordinal);
        Assert.Contains("Schlusszeile", mime.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", mime.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Calibri", mime.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MsoChpDefault", mime.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("should not appear", mime.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Meeting notes", mime.TextBody, StringComparison.Ordinal);

        var html = mime.HtmlBody!;
        Assert.Contains("Grüße", System.Net.WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        Assert.DoesNotContain("@font-face", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<head", html, StringComparison.OrdinalIgnoreCase);
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
            TextBody = "Danke schön — Grüße",
            QuoteOriginal = true,
            ReplyToMessageId = "9"
        };

        var mime = await MailKitEmailService.BuildDraftMimeAsync(
            request, "mattanja@kern.services", original, CancellationToken.None);

        // Real MIME round-trip: write → parse → charset + content survive.
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream);
        stream.Position = 0;
        var parsed = await MimeMessage.LoadAsync(stream);

        Assert.Equal("Re: Größe", parsed.Subject);
        Assert.Contains("Grüße", parsed.TextBody, StringComparison.Ordinal);
        Assert.Contains("Schöne Grüße aus Köln", parsed.TextBody, StringComparison.Ordinal);

        var textPart = parsed.BodyParts.OfType<TextPart>().First(p => p.IsPlain);
        var htmlPart = parsed.BodyParts.OfType<TextPart>().First(p => p.IsHtml);
        Assert.Contains("utf-8", textPart.ContentType.Charset ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("utf-8", htmlPart.ContentType.Charset ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var htmlDecoded = System.Net.WebUtility.HtmlDecode(parsed.HtmlBody);
        Assert.Contains("Grüße", htmlDecoded, StringComparison.Ordinal);
        Assert.Contains("Schöne Grüße aus Köln", htmlDecoded, StringComparison.Ordinal);
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
