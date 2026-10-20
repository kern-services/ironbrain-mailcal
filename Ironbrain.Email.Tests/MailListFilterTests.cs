using MailKit.Search;
using Xunit;

namespace Ironbrain.Email.Tests;

public class MailListFilterTests
{
    [Fact]
    public void BuildListSearchQuery_UnreadTrue_UsesNotSeen()
    {
        var query = MailKitEmailService.BuildListSearchQuery(new EmailListFilter { Unread = true });
        Assert.Equal(SearchQuery.NotSeen.Term, query.Term);
    }

    [Fact]
    public void BuildListSearchQuery_CombinesFromAndSubject()
    {
        var query = MailKitEmailService.BuildListSearchQuery(new EmailListFilter
        {
            FromContains = "alice@",
            SubjectContains = "invoice"
        });

        // Binary And — term is And
        Assert.Equal(SearchTerm.And, query.Term);
    }

    [Fact]
    public void ParseUids_DedupesAndPreservesOrder()
    {
        var uids = MailKitEmailService.ParseUids(["3", "1", "3", "2"]);
        Assert.Equal([3u, 1u, 2u], uids.Select(u => u.Id).ToArray());
    }

    [Fact]
    public void ParseUids_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => MailKitEmailService.ParseUids([]));
    }

    [Fact]
    public void EmailListFilter_HasAny_FalseWhenEmpty()
    {
        Assert.False(new EmailListFilter().HasAny);
        Assert.True(new EmailListFilter { Unread = false }.HasAny);
        Assert.True(new EmailListFilter { FromContains = "x" }.HasAny);
    }
}
