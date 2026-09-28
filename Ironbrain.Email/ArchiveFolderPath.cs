namespace Ironbrain.Email;

/// <summary>
/// Expands per-account IMAP archive folder patterns such as <c>Archive/{CurrentYear}</c>
/// (default) or <c>Archive/{YYYY}</c> / <c>Archive/{YYYY}/{MM}/</c> (mox-style year folders).
/// </summary>
public static class ArchiveFolderPath
{
    /// <summary>
    /// Pattern used when <see cref="ImapOptions.ArchiveFolder"/> is unset or empty.
    /// </summary>
    public const string DefaultPattern = "Archive/{CurrentYear}";

    /// <summary>
    /// Expands <paramref name="pattern"/> placeholders.
    /// When <paramref name="pattern"/> is null/whitespace, uses <see cref="DefaultPattern"/>.
    /// <c>{CurrentYear}</c> always expands from <paramref name="utcNow"/> (UTC clock; defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>) — never from the message Date.
    /// <c>{YYYY}</c>, <c>{YY}</c>, <c>{MM}</c>, <c>{DD}</c> expand from the message date when present
    /// and usable; otherwise the same UTC clock. Tokens are case-insensitive.
    /// </summary>
    public static string Expand(string? pattern, DateTimeOffset? messageDate, DateTimeOffset? utcNow = null)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        var clock = utcNow ?? DateTimeOffset.UtcNow;
        var when = ResolveDate(messageDate, clock);
        var expanded = effective
            .Replace("{CurrentYear}", clock.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
            .Replace("{YYYY}", when.ToString("yyyy"), StringComparison.OrdinalIgnoreCase)
            .Replace("{YY}", when.ToString("yy"), StringComparison.OrdinalIgnoreCase)
            .Replace("{MM}", when.ToString("MM"), StringComparison.OrdinalIgnoreCase)
            .Replace("{DD}", when.ToString("dd"), StringComparison.OrdinalIgnoreCase);

        return expanded.TrimEnd('/', '.', ' ');
    }

    /// <summary>
    /// Uses <paramref name="messageDate"/> when it has a value other than <see cref="DateTimeOffset.MinValue"/>;
    /// otherwise falls back to <paramref name="utcNow"/> or UTC now.
    /// </summary>
    public static DateTimeOffset ResolveDate(DateTimeOffset? messageDate, DateTimeOffset? utcNow = null)
    {
        if (messageDate is { } d && d != DateTimeOffset.MinValue)
            return d;
        return utcNow ?? DateTimeOffset.UtcNow;
    }
}
