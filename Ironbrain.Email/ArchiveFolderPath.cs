namespace Ironbrain.Email;

/// <summary>
/// Expands per-account IMAP archive folder patterns such as <c>Archive/{YYYY}</c>
/// (common on mox-style year folders) or <c>Archive/{YYYY}/{MM}/</c>.
/// </summary>
public static class ArchiveFolderPath
{
    /// <summary>
    /// Expands <paramref name="pattern"/> placeholders using the message date when present and usable;
    /// otherwise <paramref name="utcNow"/> (defaults to <see cref="DateTimeOffset.UtcNow"/>).
    /// Supported tokens: <c>{YYYY}</c>, <c>{YY}</c>, <c>{MM}</c>, <c>{DD}</c> (case-insensitive).
    /// </summary>
    public static string Expand(string pattern, DateTimeOffset? messageDate, DateTimeOffset? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Archive folder pattern must not be empty.", nameof(pattern));

        var when = ResolveDate(messageDate, utcNow);
        var expanded = pattern.Trim()
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
