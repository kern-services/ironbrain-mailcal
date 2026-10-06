using Ironbrain.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailForwardTests
{
    [Theory]
    [InlineData(null, "Invoice", "Fwd: Invoice")]
    [InlineData("", "Invoice", "Fwd: Invoice")]
    [InlineData("  ", "Invoice", "Fwd: Invoice")]
    [InlineData("Custom", "Invoice", "Custom")]
    [InlineData(null, "Fw: Invoice", "Fw: Invoice")]
    [InlineData(null, "Fwd: Invoice", "Fwd: Invoice")]
    [InlineData(null, null, "Fwd:")]
    [InlineData(null, "", "Fwd:")]
    public void ResolveForwardSubject_AppliesFwdPrefixOrOverride(
        string? requested,
        string? original,
        string expected)
    {
        Assert.Equal(expected, MailKitEmailService.ResolveForwardSubject(requested, original));
    }

    [Fact]
    public void FormatFolderNotFound_IncludesAccountHostFolder()
    {
        var msg = MailKitEmailService.FormatFolderNotFound(
            "Archive/2026",
            new ImapOptions { Host = "imap.example.com", Username = "assistant@example.com" },
            accountId: null,
            operation: "ForwardEmail IMAP source");

        Assert.Contains("Archive/2026", msg, StringComparison.Ordinal);
        Assert.Contains("assistant@example.com", msg, StringComparison.Ordinal);
        Assert.Contains("imap.example.com", msg, StringComparison.Ordinal);
        Assert.Contains("sourceAccountId", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderPathCandidates_IncludesSlashDotAlternates()
    {
        var candidates = MailKitEmailService.FolderPathCandidates("Archive/2026");
        Assert.Equal("Archive/2026", candidates[0]);
        Assert.Contains("Archive.2026", candidates);
    }

    [Fact]
    public async Task BuildForwardBodyAsync_IncludesNoteHeaderAndAttachments()
    {
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("billing@example.com"));
        original.Subject = "Hetzner Invoice";
        original.Date = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var originalBuilder = new BodyBuilder
        {
            TextBody = "Please find the invoice attached."
        };
        originalBuilder.Attachments.Add("invoice.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }, new ContentType("application", "pdf"));
        original.Body = originalBuilder.ToMessageBody();

        var forward = await MailKitEmailService.BuildForwardBodyAsync(original, "For Lexware", CancellationToken.None);

        Assert.Contains("For Lexware", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("---------- Forwarded message ----------", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("billing@example.com", forward.TextBody, StringComparison.Ordinal);
        Assert.Contains("Please find the invoice attached.", forward.TextBody, StringComparison.Ordinal);
        Assert.Single(forward.Attachments);
        Assert.Equal("invoice.pdf", forward.Attachments[0].ContentDisposition?.FileName
            ?? forward.Attachments[0].ContentType.Name);
    }

    [Fact]
    public void CollectAttachmentInfos_ReturnsFileNameAndType()
    {
        var message = new MimeMessage();
        var builder = new BodyBuilder { TextBody = "hi" };
        builder.Attachments.Add("a.txt", "hello"u8.ToArray(), new ContentType("text", "plain"));
        message.Body = builder.ToMessageBody();

        var infos = MailKitEmailService.CollectAttachmentInfos(message);

        var info = Assert.Single(infos);
        Assert.Equal("a.txt", info.FileName);
        Assert.Equal("text/plain", info.ContentType);
        Assert.True(info.Size >= 0);
    }

    [Fact]
    public async Task ForwardEmailAsync_InvalidUid_ThrowsBeforeConnect()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardEmailAsync(new EmailForwardRequest
            {
                EmailId = "not-a-uid",
                To = "kern.services@inbox.lexware.email"
            }));

        Assert.Contains("Invalid IMAP UID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardEmailAsync_EmptyTo_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ForwardEmailAsync(new EmailForwardRequest
            {
                EmailId = "1",
                To = "  "
            }));
    }

    private static MailKitEmailService CreateService()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new MailKitEmailService(
            Options.Create(new SmtpOptions
            {
                Enabled = true,
                Host = "smtp.example.invalid",
                FromAddress = "noreply@example.com",
                FromName = "Test"
            }),
            Options.Create(new ImapOptions
            {
                Host = "imap.example.invalid",
                Username = "u",
                Password = "p",
                Mailbox = "INBOX"
            }),
            services,
            NullLogger<MailKitEmailService>.Instance);
    }
}
