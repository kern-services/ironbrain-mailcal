using Ironbrain.Email;
using Xunit;

namespace Ironbrain.Email.Tests;

public class ArchiveFolderPathTests
{
    private static readonly DateTimeOffset SampleDate = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FallbackUtc = new(2099, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Archive/{YYYY}", "Archive/2026")]
    [InlineData("Archive/{YYYY}/{MM}/", "Archive/2026/03")]
    [InlineData("Archive/{YY}/{MM}/{DD}", "Archive/26/03/15")]
    [InlineData("INBOX.Archive.{YYYY}", "INBOX.Archive.2026")]
    public void Expand_UsesMessageDate(string pattern, string expected)
    {
        var actual = ArchiveFolderPath.Expand(pattern, SampleDate, FallbackUtc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Expand_FallsBackToUtcNow_WhenDateMissing()
    {
        var actual = ArchiveFolderPath.Expand("Archive/{YYYY}/{MM}", messageDate: null, utcNow: FallbackUtc);
        Assert.Equal("Archive/2099/12", actual);
    }

    [Fact]
    public void Expand_FallsBackToUtcNow_WhenDateIsMinValue()
    {
        var actual = ArchiveFolderPath.Expand("Archive/{YYYY}", DateTimeOffset.MinValue, FallbackUtc);
        Assert.Equal("Archive/2099", actual);
    }

    [Fact]
    public void Expand_TokensAreCaseInsensitive()
    {
        var actual = ArchiveFolderPath.Expand("Archive/{yyyy}/{mm}", SampleDate, FallbackUtc);
        Assert.Equal("Archive/2026/03", actual);
    }

    [Fact]
    public void Expand_EmptyPattern_Throws()
    {
        Assert.Throws<ArgumentException>(() => ArchiveFolderPath.Expand("  ", SampleDate));
    }

    [Fact]
    public void ResolveDate_PrefersMessageDate()
    {
        Assert.Equal(SampleDate, ArchiveFolderPath.ResolveDate(SampleDate, FallbackUtc));
    }

    [Fact]
    public void ResolveDate_NullFallsBack()
    {
        Assert.Equal(FallbackUtc, ArchiveFolderPath.ResolveDate(null, FallbackUtc));
    }
}
