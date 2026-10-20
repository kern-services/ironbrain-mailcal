using MailKit;
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
    public void BuildListSearchQuery_SinceBefore_NoDayWidening_BeforeExclusive()
    {
        var since = new DateTimeOffset(2026, 10, 20, 15, 30, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        var query = MailKitEmailService.BuildListSearchQuery(new EmailListFilter
        {
            Since = since,
            Before = before
        });

        Assert.Equal(SearchTerm.And, query.Term);
        var binary = Assert.IsType<BinarySearchQuery>(query);
        var sinceQ = Assert.IsType<DateSearchQuery>(binary.Left);
        var beforeQ = Assert.IsType<DateSearchQuery>(binary.Right);
        Assert.Equal(SearchTerm.DeliveredAfter, sinceQ.Term);
        Assert.Equal(SearchTerm.DeliveredBefore, beforeQ.Term);
        // SINCE inclusive of since's UTC calendar day; BEFORE exclusive of before's UTC calendar day.
        Assert.Equal(new DateTime(2026, 10, 20), sinceQ.Date.Date);
        Assert.Equal(new DateTime(2026, 10, 22), beforeQ.Date.Date);
        // Must not widen (±1 day) vs docs/MCP/CLI.
        Assert.NotEqual(new DateTime(2026, 10, 19), sinceQ.Date.Date);
        Assert.NotEqual(new DateTime(2026, 10, 23), beforeQ.Date.Date);
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
    public void ResolveMovedSourceUids_UsesMapSource_NotRequestedCount()
    {
        var requested = new List<UniqueId> { new(1), new(2), new(3) };
        var map = new UniqueIdMap(
            source: [new UniqueId(1), new UniqueId(3)],
            destination: [new UniqueId(10), new UniqueId(11)]);

        var moved = MailKitEmailService.ResolveMovedSourceUids(map, requested);
        Assert.Equal([1u, 3u], moved.Select(u => u.Id).ToArray());
        Assert.Equal(2, moved.Count);
        Assert.NotEqual(requested.Count, moved.Count);
    }

    [Fact]
    public void ResolveMovedSourceUids_EmptyMap_FallsBackToRequested()
    {
        var requested = new List<UniqueId> { new(5), new(6) };
        var moved = MailKitEmailService.ResolveMovedSourceUids(UniqueIdMap.Empty, requested);
        Assert.Equal([5u, 6u], moved.Select(u => u.Id).ToArray());
    }

    [Fact]
    public void EmailListFilter_HasAny_FalseWhenEmpty()
    {
        Assert.False(new EmailListFilter().HasAny);
        Assert.True(new EmailListFilter { Unread = false }.HasAny);
        Assert.True(new EmailListFilter { FromContains = "x" }.HasAny);
    }
}
