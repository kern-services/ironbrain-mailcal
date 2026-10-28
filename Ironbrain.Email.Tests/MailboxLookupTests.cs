using Ironbrain.Email;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailboxLookupTests
{
    [Fact]
    public void ResolveFolderName_ExactMatch_Preferred()
    {
        var names = new[] { "Introbox", "introbox", "INBOX" };
        Assert.Equal("introbox", MailKitEmailService.ResolveFolderName(names, "introbox"));
    }

    [Fact]
    public void ResolveFolderName_DifferentCase_Resolves()
    {
        var names = new[] { "INBOX", "Introbox", "Sent" };
        Assert.Equal("Introbox", MailKitEmailService.ResolveFolderName(names, "introbox"));
    }

    [Fact]
    public void ResolveFolderName_Missing_ReturnsNull()
    {
        var names = new[] { "INBOX", "Introbox", "Sent" };
        Assert.Null(MailKitEmailService.ResolveFolderName(names, "nosuchfolder"));
    }

    [Fact]
    public void MailboxNotFoundException_Message_IsClear()
    {
        var ex = new MailboxNotFoundException("introbox");
        Assert.Equal("introbox", ex.Mailbox);
        Assert.Equal("Mailbox 'introbox' was not found.", ex.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void NormalizeMailboxPath_TrimsTrailingSeparators()
    {
        Assert.Equal("Introbox", MailKitEmailService.NormalizeMailboxPath(" Introbox/ "));
    }
}
