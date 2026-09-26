using Ironbrain.Calendar;
using System.Xml.Linq;
using Xunit;

namespace Ironbrain.Calendar.Tests;

public class CalendarCollectionHelperTests
{
    [Fact]
    public void InferHomeUrl_FromCollection_ReturnsParent()
    {
        var home = CalendarCollectionHelper.InferHomeUrl(
            "https://mail.example.com/SOGo/dav/user@example.com/Calendar/personal/");
        Assert.Equal("https://mail.example.com/SOGo/dav/user@example.com/Calendar/", home);
    }

    [Fact]
    public void InferHomeUrl_FromIcs_ReturnsNull()
    {
        Assert.Null(CalendarCollectionHelper.InferHomeUrl("https://mail.example.com/feed.ics"));
    }

    [Theory]
    [InlineData("https://x/Calendar/personal.ics", true)]
    [InlineData("https://x/Calendar/personal.xml", true)]
    [InlineData("https://x/Calendar/personal/", false)]
    [InlineData("https://x/Calendar/9536E-ABC/", false)]
    public void IsMirrorHref_DetectsIcsAndXml(string href, bool expected) =>
        Assert.Equal(expected, CalendarCollectionHelper.IsMirrorHref(href));

    [Fact]
    public void ParsePropfindMultistatus_FiltersMirrorsAndHome()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/SOGo/dav/user@example.com/Calendar/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Calendar</d:displayname>
                    <d:resourcetype><d:collection/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/SOGo/dav/user@example.com/Calendar/personal/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Personal</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:owner><d:href>/SOGo/dav/user@example.com/</d:href></d:owner>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/SOGo/dav/user@example.com/Calendar/personal.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Personal ICS</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/SOGo/dav/user@example.com/Calendar/Family/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Family</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/SOGo/dav/user@example.com/Calendar/alice_shared/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Alice</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:owner><d:href>/SOGo/dav/alice.smith@example.com/</d:href></d:owner>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var home = "https://mail.example.com/SOGo/dav/user@example.com/Calendar/";
        var list = CalendarCollectionHelper.ParsePropfindMultistatus(xml, home, "user@example.com");

        Assert.Equal(3, list.Count);
        Assert.DoesNotContain(list, c => c.Href.Contains(".ics", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, c => c.DisplayName == "Personal" && c.CollectionId == "personal" && !c.IsShared);
        Assert.Contains(list, c => c.DisplayName == "Family" && c.CollectionId == "Family");
        Assert.Contains(list, c => c.DisplayName == "Alice" && c.IsShared);
    }

    [Fact]
    public void MatchesFilter_ByDisplayNameOrCollectionId()
    {
        var cal = new CalendarCollectionInfo
        {
            DisplayName = "personal",
            Href = "https://mail.example.com/SOGo/dav/u/Calendar/9536E-ABC/",
            CollectionId = "9536E-ABC",
            IsShared = false
        };
        Assert.True(CalendarCollectionHelper.MatchesFilter(cal, "personal"));
        Assert.True(CalendarCollectionHelper.MatchesFilter(cal, "9536E-ABC"));
        Assert.False(CalendarCollectionHelper.MatchesFilter(cal, "Family"));
    }

    [Fact]
    public void ApplyFilters_CommaSeparated_MatchesSubset()
    {
        var all = SampleCollections();
        var matched = CalendarCollectionHelper.ApplyFilters(all, ["Family,personal"], includeShared: true);
        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, c => c.CollectionId == "personal");
        Assert.Contains(matched, c => c.CollectionId == "Family");
    }

    [Fact]
    public void ApplyFilters_NoMatch_ThrowsClearError()
    {
        var all = SampleCollections();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CalendarCollectionHelper.ApplyFilters(all, ["Nope"], includeShared: true));
        Assert.Contains("No calendars matched", ex.Message);
        Assert.Contains("Known:", ex.Message);
    }

    [Fact]
    public void ApplyFilters_ExcludeShared()
    {
        var all = SampleCollections();
        var matched = CalendarCollectionHelper.ApplyFilters(all, filters: null, includeShared: false);
        Assert.DoesNotContain(matched, c => c.IsShared);
        Assert.Equal(2, matched.Count);
    }

    [Fact]
    public void ResolveWriteTarget_WithoutSelector_Multiple_Throws()
    {
        var all = SampleCollections();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CalendarCollectionHelper.ResolveWriteTarget(all, selector: null, defaultWriteCalendar: null, explicitUrl: null));
        Assert.Contains("Specify --calendar/-c", ex.Message);
        Assert.Contains("never target all", ex.Message);
    }

    [Fact]
    public void ResolveWriteTarget_UsesDefaultWriteCalendar()
    {
        var all = SampleCollections();
        var target = CalendarCollectionHelper.ResolveWriteTarget(
            all, selector: null, defaultWriteCalendar: "Family", explicitUrl: null);
        Assert.Equal("Family", target.DisplayName);
    }

    [Fact]
    public void ResolveWriteTarget_UsesSelector()
    {
        var all = SampleCollections();
        var target = CalendarCollectionHelper.ResolveWriteTarget(
            all, selector: "personal", defaultWriteCalendar: "Family", explicitUrl: null);
        Assert.Equal("personal", target.CollectionId);
    }

    [Fact]
    public void ResolveWriteTarget_SingleCalendar_OkWithoutSelector()
    {
        var one = new List<CalendarCollectionInfo> { SampleCollections()[0] };
        var target = CalendarCollectionHelper.ResolveWriteTarget(
            one, selector: null, defaultWriteCalendar: null, explicitUrl: null);
        Assert.Equal("personal", target.CollectionId);
    }

    [Fact]
    public void EnsureCollectionUrl_AddsTrailingSlash()
    {
        Assert.Equal(
            "https://mail.example.com/SOGo/dav/u/Calendar/",
            CalendarCollectionHelper.EnsureCollectionUrl("https://mail.example.com/SOGo/dav/u/Calendar"));
        Assert.Equal(
            "https://mail.example.com/SOGo/dav/u/Calendar/",
            CalendarCollectionHelper.EnsureCollectionUrl("https://mail.example.com/SOGo/dav/u/Calendar/"));
    }

    [Fact]
    public void ParsePropfind_SogoFixture_IncludesSharedDelegatedCalendar()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sogo-propfind-with-shared.xml"));
        var home = "https://mail.example.com/SOGo/dav/user@example.com/Calendar/";
        var list = CalendarCollectionHelper.ParsePropfindMultistatus(xml, home, "user@example.com");

        Assert.Equal(7, list.Count);
        var alice = Assert.Single(list, c =>
            c.CollectionId == "alice_D_smith_A_example_D_com_personal");
        Assert.True(alice.IsShared);
        Assert.Contains("Alice", alice.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice.smith@example.com", alice.DisplayName, StringComparison.OrdinalIgnoreCase);

        Assert.All(
            list.Where(c => c.CollectionId != "alice_D_smith_A_example_D_com_personal"),
            c => Assert.False(c.IsShared));

        var included = CalendarCollectionHelper.ApplyFilters(list, null, includeShared: true);
        var excluded = CalendarCollectionHelper.ApplyFilters(list, null, includeShared: false);
        Assert.Equal(7, included.Count);
        Assert.Equal(6, excluded.Count);
        Assert.DoesNotContain(excluded, c => c.IsShared);
    }

    [Fact]
    public void DetectShared_SogoEncodedCollectionId_WithoutOwner_UsesDecodedEmail()
    {
        var href = "https://mail.example.com/SOGo/dav/user@example.com/Calendar/alice_D_smith_A_example_D_com_personal/";
        Assert.True(CalendarCollectionHelper.DetectShared(href, ownerHref: null, "user@example.com"));
        Assert.False(CalendarCollectionHelper.DetectShared(
            href,
            ownerHref: "/SOGo/dav/user@example.com/",
            "user@example.com"));
    }

    [Fact]
    public void DetectShared_OwnerPrincipalDifferentFromLogin_IsShared()
    {
        Assert.True(CalendarCollectionHelper.DetectShared(
            "https://mail.example.com/SOGo/dav/user@example.com/Calendar/alice_D_smith_A_example_D_com_personal/",
            "/SOGo/dav/alice.smith@example.com/",
            "user@example.com"));
    }

    [Fact]
    public void DetectShared_DoesNotFalsePositiveOnDomainSubstring()
    {
        // Username "smith" must not match owner alice.smith@… via naive Contains
        Assert.True(CalendarCollectionHelper.DetectShared(
            "https://x/Calendar/alice_D_smith_A_example_D_com_personal/",
            "/SOGo/dav/alice.smith@example.com/",
            "smith"));
    }

    [Fact]
    public void TryDecodeSogoEncodedEmail_DecodesDelegatedId()
    {
        Assert.True(CalendarCollectionHelper.TryDecodeSogoEncodedEmail(
            "alice_D_smith_A_example_D_com_personal", out var email, out var suffix));
        Assert.Equal("alice.smith@example.com", email);
        Assert.Equal("personal", suffix);
    }

    [Fact]
    public void SelectSuccessfulProp_Skips404Propstat()
    {
        var xml = XDocument.Parse("""
            <d:response xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:href>/Calendar/shared/</d:href>
              <d:propstat>
                <d:status>HTTP/1.1 404 Not Found</d:status>
                <d:prop><d:getetag/></d:prop>
              </d:propstat>
              <d:propstat>
                <d:status>HTTP/1.1 200 OK</d:status>
                <d:prop>
                  <d:displayname>Shared</d:displayname>
                  <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                </d:prop>
              </d:propstat>
            </d:response>
            """);
        var prop = CalendarCollectionHelper.SelectSuccessfulProp(xml.Root!);
        Assert.NotNull(prop);
        Assert.Equal("Shared", prop.Element(XName.Get("displayname", "DAV:"))?.Value);
        Assert.NotNull(prop.Element(XName.Get("resourcetype", "DAV:")));
    }

    private static List<CalendarCollectionInfo> SampleCollections() =>
    [
        new()
        {
            DisplayName = "Personal",
            Href = "https://mail.example.com/SOGo/dav/u/Calendar/personal",
            CollectionId = "personal",
            IsShared = false
        },
        new()
        {
            DisplayName = "Family",
            Href = "https://mail.example.com/SOGo/dav/u/Calendar/Family",
            CollectionId = "Family",
            IsShared = false
        },
        new()
        {
            DisplayName = "Alice",
            Href = "https://mail.example.com/SOGo/dav/u/Calendar/alice_shared",
            CollectionId = "alice_shared",
            IsShared = true
        }
    ];
}
