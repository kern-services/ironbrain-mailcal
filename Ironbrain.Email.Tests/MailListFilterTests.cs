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
    public void BuildListSearchQuery_SinceBefore_WidensDayWindow()
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
        // Coarse IMAP window: SINCE (utcDate−1), BEFORE (utcDate+2); exact bounds after fetch.
        Assert.Equal(new DateTime(2026, 10, 19), sinceQ.Date.Date);
        Assert.Equal(new DateTime(2026, 10, 24), beforeQ.Date.Date);
    }

    [Fact]
    public void BuildListSearchQuery_BeforeOffset_SearchesPastUtcDay()
    {
        // before=2026-10-21T10:40+02:00 → UTC day 21-Oct; must not SEARCH BEFORE 21-Oct
        // (that would drop same-day 10:27 mail before the exact-time filter runs).
        var before = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00");
        var query = MailKitEmailService.BuildListSearchQuery(new EmailListFilter { Before = before });

        var beforeQ = Assert.IsType<DateSearchQuery>(query);
        Assert.Equal(SearchTerm.DeliveredBefore, beforeQ.Term);
        Assert.Equal(new DateTime(2026, 10, 21), before.UtcDateTime.Date);
        Assert.True(beforeQ.Date.Date > new DateTime(2026, 10, 21),
            "IMAP BEFORE must be after the UTC day of the bound so Oct 21 candidates are returned");
        Assert.Equal(new DateTime(2026, 10, 23), beforeQ.Date.Date);
    }

    [Fact]
    public void PassesExactTimeFilter_Iso8601Since_ExcludesEarlierSameDay()
    {
        // IMAP day search would still return 10:27; exact filter must drop it.
        var since = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00");
        var earlier = DateTimeOffset.Parse("2026-10-21T10:27:00+02:00");
        var onBoundary = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00");
        var later = DateTimeOffset.Parse("2026-10-21T11:00:00+02:00");

        Assert.False(MailKitEmailService.PassesExactTimeFilter(earlier, since, before: null));
        Assert.True(MailKitEmailService.PassesExactTimeFilter(onBoundary, since, before: null));
        Assert.True(MailKitEmailService.PassesExactTimeFilter(later, since, before: null));
        Assert.True(MailKitEmailService.NeedsExactTimeFilter(new EmailListFilter { Since = since }));
    }

    [Fact]
    public void PassesExactTimeFilter_BeforeExclusive_DropsOnAndAfterBound()
    {
        var before = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00");
        var earlier = DateTimeOffset.Parse("2026-10-21T10:27:00+02:00");
        var onBoundary = before;

        Assert.True(MailKitEmailService.PassesExactTimeFilter(earlier, since: null, before));
        Assert.False(MailKitEmailService.PassesExactTimeFilter(onBoundary, since: null, before));
    }

    [Fact]
    public void PassesExactTimeFilter_MissingDate_ExcludedWhenAnyBoundSet()
    {
        var since = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00");
        var before = since;

        // Unknown date must not match since-only, before-only, or both.
        Assert.False(MailKitEmailService.PassesExactTimeFilter(null, since, before: null));
        Assert.False(MailKitEmailService.PassesExactTimeFilter(null, since: null, before));
        Assert.False(MailKitEmailService.PassesExactTimeFilter(null, since, before));
        // No bounds: listing path does not call this, but null still "passes".
        Assert.True(MailKitEmailService.PassesExactTimeFilter(null, since: null, before: null));
    }

    [Fact]
    public void ResolveListFilterDate_FallsBackToEnvelopeDate()
    {
        var envelope = DateTimeOffset.Parse("2026-10-21T10:27:00+02:00");
        Assert.Equal(envelope, MailKitEmailService.ResolveListFilterDate(null, envelope));
        Assert.Null(MailKitEmailService.ResolveListFilterDate(null, null));

        var internalDate = DateTimeOffset.Parse("2026-10-21T09:00:00+02:00");
        Assert.Equal(internalDate, MailKitEmailService.ResolveListFilterDate(internalDate, envelope));
    }

    [Fact]
    public void ListTimeFilterFixture_BeforeOffset_WideSearchKeepsEarlierSameDay()
    {
        // IMAP coarse window must return Oct 21 candidates; exact filter keeps 10:27, drops 10:40+.
        var filter = new EmailListFilter
        {
            Before = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00")
        };
        var query = MailKitEmailService.BuildListSearchQuery(filter);
        var beforeQ = Assert.IsType<DateSearchQuery>(query);
        Assert.Equal(new DateTime(2026, 10, 23), beforeQ.Date.Date);

        DateTimeOffset?[] candidates =
        [
            DateTimeOffset.Parse("2026-10-21T10:27:00+02:00"),
            DateTimeOffset.Parse("2026-10-21T10:40:00+02:00"),
            DateTimeOffset.Parse("2026-10-21T11:00:00+02:00"),
            null // no INTERNALDATE / Envelope.Date → excluded by before
        ];

        var kept = MailKitEmailService.ApplyListTimeFilters(candidates, filter);
        Assert.Single(kept);
        Assert.Equal(candidates[0], kept[0]);
    }

    [Fact]
    public void ListTimeFilterFixture_BeforeUtcMidnight_KeepsPriorUtcEveningAcrossTz()
    {
        // Message at 20-Oct 23:30Z may be 21-Oct in +02 on the server; coarse BEFORE must
        // reach utcDate+2 so the candidate survives SEARCH, then exact filter keeps it for
        // before=2026-10-21T00:00Z (exclusive upper).
        var filter = new EmailListFilter
        {
            Before = DateTimeOffset.Parse("2026-10-21T00:00:00Z")
        };
        var query = MailKitEmailService.BuildListSearchQuery(filter);
        var beforeQ = Assert.IsType<DateSearchQuery>(query);
        Assert.Equal(new DateTime(2026, 10, 23), beforeQ.Date.Date);

        var priorEveningUtc = DateTimeOffset.Parse("2026-10-20T23:30:00Z");
        var onBound = DateTimeOffset.Parse("2026-10-21T00:00:00Z");
        var kept = MailKitEmailService.ApplyListTimeFilters([priorEveningUtc, onBound], filter);
        Assert.Single(kept);
        Assert.Equal(priorEveningUtc, kept[0]);
    }

    [Fact]
    public void ListTimeFilterFixture_SinceWidened_ExactDropsEarlierSameDay()
    {
        var filter = new EmailListFilter
        {
            Since = DateTimeOffset.Parse("2026-10-21T10:40:00+02:00")
        };
        var query = MailKitEmailService.BuildListSearchQuery(filter);
        var sinceQ = Assert.IsType<DateSearchQuery>(query);
        Assert.Equal(new DateTime(2026, 10, 20), sinceQ.Date.Date);

        DateTimeOffset?[] candidates =
        [
            DateTimeOffset.Parse("2026-10-21T10:27:00+02:00"),
            DateTimeOffset.Parse("2026-10-21T10:40:00+02:00"),
            null
        ];

        var kept = MailKitEmailService.ApplyListTimeFilters(candidates, filter);
        Assert.Single(kept);
        Assert.Equal(candidates[1], kept[0]);
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
