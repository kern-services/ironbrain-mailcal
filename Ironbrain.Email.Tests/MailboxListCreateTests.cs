using MailKit;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailboxListCreateTests
{
    [Fact]
    public void MapSpecialUseFlags_InboxAndTrash()
    {
        var flags = MailKitEmailService.MapSpecialUseFlags(
            FolderAttributes.Inbox | FolderAttributes.Trash);
        Assert.Equal(new[] { @"\Inbox", @"\Trash" }, flags);
    }

    [Fact]
    public void MapSpecialUseFlags_None_ReturnsEmpty()
    {
        Assert.Empty(MailKitEmailService.MapSpecialUseFlags(FolderAttributes.None));
    }

    [Fact]
    public void MapSpecialUseFlags_AllRoles()
    {
        var flags = MailKitEmailService.MapSpecialUseFlags(
            FolderAttributes.Inbox
            | FolderAttributes.Drafts
            | FolderAttributes.Sent
            | FolderAttributes.Junk
            | FolderAttributes.Trash
            | FolderAttributes.Archive
            | FolderAttributes.All
            | FolderAttributes.Flagged
            | FolderAttributes.Important);

        Assert.Equal(
            new[]
            {
                @"\Inbox",
                @"\Drafts",
                @"\Sent",
                @"\Junk",
                @"\Trash",
                @"\Archive",
                @"\All",
                @"\Flagged",
                @"\Important"
            },
            flags);
    }

    [Fact]
    public void CreateMailboxResult_CreatedFalse_MeansIdempotentHit()
    {
        var result = new CreateMailboxResult
        {
            FullName = "Projects/Foo",
            Delimiter = "/",
            Created = false
        };
        Assert.False(result.Created);
        Assert.Equal("Projects/Foo", result.FullName);
    }

    [Fact]
    public void MailboxInfo_CarriesDelimiterAndFlags()
    {
        var info = new MailboxInfo
        {
            Name = "INBOX",
            FullName = "INBOX",
            Delimiter = "/",
            SpecialUseFlags = [@"\Inbox"],
            CanSelect = true,
            HasChildren = false
        };
        Assert.Equal(@"\Inbox", Assert.Single(info.SpecialUseFlags));
        Assert.Equal("/", info.Delimiter);
    }
}
