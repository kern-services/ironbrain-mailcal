using Ironbrain.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailMoveArchiveWiringTests
{
    [Fact]
    public async Task ArchiveEmailAsync_WithoutArchiveFolder_ThrowsClearly_BeforeImap()
    {
        var service = CreateService(new ImapOptions
        {
            Host = "imap.example.invalid",
            Username = "u",
            Password = "not-logged",
            Mailbox = "INBOX",
            ArchiveFolder = ""
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchiveEmailAsync("42"));

        Assert.Contains("Archive folder is not configured", ex.Message);
        Assert.Contains("Archive/{YYYY}", ex.Message);
    }

    [Fact]
    public async Task ArchiveEmailAsync_WhitespaceArchiveFolder_ThrowsClearly()
    {
        var service = CreateService(new ImapOptions
        {
            Host = "imap.example.invalid",
            ArchiveFolder = "   "
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ArchiveEmailAsync("1"));

        Assert.Contains("Archive folder is not configured", ex.Message);
    }

    [Fact]
    public async Task MoveEmailAsync_EmptyDestination_Throws()
    {
        var service = CreateService(new ImapOptions { Host = "imap.example.invalid" });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.MoveEmailAsync("1", "  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ParseUid_RejectsInvalid(string id)
    {
        Assert.Throws<ArgumentException>(() => MailKitEmailService.ParseUid(id));
    }

    [Fact]
    public void ParseUid_AcceptsPositiveInteger()
    {
        var uid = MailKitEmailService.ParseUid("12345");
        Assert.Equal(12345u, uid.Id);
    }

    [Fact]
    public async Task MoveEmailAsync_InvalidUid_ThrowsBeforeConnect()
    {
        var service = CreateService(new ImapOptions
        {
            Host = "imap.example.invalid",
            Username = "u",
            Password = "secret-must-not-appear"
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.MoveEmailAsync("not-a-uid", "Archive/2026"));

        Assert.Contains("Invalid IMAP UID", ex.Message);
        Assert.DoesNotContain("secret-must-not-appear", ex.Message);
    }

    private static MailKitEmailService CreateService(ImapOptions imap)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new MailKitEmailService(
            Options.Create(new SmtpOptions()),
            Options.Create(imap),
            services,
            NullLogger<MailKitEmailService>.Instance);
    }
}
