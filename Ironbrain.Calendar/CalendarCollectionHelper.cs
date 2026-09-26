using System.Xml.Linq;

namespace Ironbrain.Calendar;

/// <summary>
/// Pure helpers for CalDAV multi-calendar discovery, filtering, and write-target resolution.
/// </summary>
public static class CalendarCollectionHelper
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";

    /// <summary>
    /// Infer calendar home from a single collection URL
    /// (e.g. <c>…/Calendar/personal/</c> → <c>…/Calendar/</c>).
    /// </summary>
    public static string? InferHomeUrl(string? sourceOrCollectionUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceOrCollectionUrl))
            return null;

        var url = NormalizeUrl(sourceOrCollectionUrl);
        if (url.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
            return null;

        // Strip trailing collection segment
        var trim = url.TrimEnd('/');
        var slash = trim.LastIndexOf('/');
        if (slash <= 0)
            return null;
        return trim[..(slash + 1)]; // keep trailing slash
    }

    public static string CollectionIdFromHref(string href)
    {
        var path = href.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    public static bool IsMirrorHref(string href)
    {
        var id = CollectionIdFromHref(href);
        return id.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)
               || id.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesFilter(CalendarCollectionInfo collection, string filter)
    {
        var f = filter.Trim();
        if (f.Length == 0)
            return false;
        if (string.Equals(collection.DisplayName, f, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(collection.CollectionId, f, StringComparison.OrdinalIgnoreCase))
            return true;
        // Allow matching a trailing path fragment inside the href
        if (collection.Href.Contains('/' + f.Trim('/') + '/', StringComparison.OrdinalIgnoreCase)
            || collection.Href.TrimEnd('/').EndsWith('/' + f.Trim('/'), StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static IReadOnlyList<CalendarCollectionInfo> ApplyFilters(
        IEnumerable<CalendarCollectionInfo> collections,
        IReadOnlyList<string>? filters,
        bool includeShared)
    {
        var list = collections
            .Where(c => includeShared || !c.IsShared)
            .ToList();

        if (filters is null || filters.Count == 0)
            return list;

        var tokens = filters
            .SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count == 0)
            return list;

        var matched = list.Where(c => tokens.Any(t => MatchesFilter(c, t))).ToList();
        if (matched.Count == 0)
        {
            var known = string.Join(", ", list.Select(c => $"{c.DisplayName} ({c.CollectionId})"));
            throw new InvalidOperationException(
                $"No calendars matched filter(s): {string.Join(", ", tokens)}. Known: {known}");
        }

        return matched;
    }

    public static CalendarCollectionInfo ResolveWriteTarget(
        IReadOnlyList<CalendarCollectionInfo> collections,
        string? selector,
        string? defaultWriteCalendar,
        string? explicitUrl)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            var url = NormalizeUrl(explicitUrl);
            var id = CollectionIdFromHref(url);
            return new CalendarCollectionInfo
            {
                DisplayName = id,
                Href = url,
                CollectionId = id,
                IsShared = false
            };
        }

        var pick = selector?.Trim();
        if (string.IsNullOrEmpty(pick))
            pick = defaultWriteCalendar?.Trim();

        if (!string.IsNullOrEmpty(pick))
        {
            var match = collections.FirstOrDefault(c => MatchesFilter(c, pick));
            if (match is null)
            {
                var known = string.Join(", ", collections.Select(c => $"{c.DisplayName} ({c.CollectionId})"));
                throw new InvalidOperationException(
                    $"Write calendar '{pick}' not found. Pass --calendar/-c or set Calendar:DefaultWriteCalendar. Known: {known}");
            }

            return match;
        }

        if (collections.Count == 1)
            return collections[0];

        if (collections.Count == 0)
            throw new InvalidOperationException("No calendars available to write to.");

        var names = string.Join(", ", collections.Select(c => c.DisplayName));
        throw new InvalidOperationException(
            $"Multiple calendars available ({names}). Specify --calendar/-c or Calendar:DefaultWriteCalendar. Writes never target all calendars.");
    }

    public static string NormalizeUrl(string url)
    {
        var u = url.Trim();
        if (u.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u["webcal://".Length..];
        else if (u.StartsWith("webdav://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u["webdav://".Length..];
        return u.TrimEnd('/');
    }

    /// <summary>
    /// CalDAV collection/home URLs should be requested with a trailing slash.
    /// SOGo may omit delegated/shared collections from Depth:1 PROPFIND when the slash is missing.
    /// </summary>
    public static string EnsureCollectionUrl(string url) => NormalizeUrl(url) + "/";

    /// <summary>Parse CalDAV PROPFIND multistatus into calendar collections (excludes .ics/.xml mirrors).</summary>
    public static IReadOnlyList<CalendarCollectionInfo> ParsePropfindMultistatus(
        string multistatusXml,
        string homeUrl,
        string? accountUsername)
    {
        var home = EnsureCollectionUrl(homeUrl);
        var doc = XDocument.Parse(multistatusXml);
        var results = new List<CalendarCollectionInfo>();

        foreach (var response in doc.Descendants(Dav + "response"))
        {
            var hrefEl = response.Element(Dav + "href");
            if (hrefEl is null || string.IsNullOrWhiteSpace(hrefEl.Value))
                continue;

            var href = ResolveHref(home, hrefEl.Value.Trim());
            if (IsMirrorHref(href))
                continue;

            // Skip the home collection itself
            if (string.Equals(EnsureCollectionUrl(href), home, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NormalizeUrl(href), NormalizeUrl(homeUrl), StringComparison.OrdinalIgnoreCase))
                continue;

            var prop = SelectSuccessfulProp(response);
            if (prop is null)
                continue;

            var resourceType = prop.Element(Dav + "resourcetype");
            var isCalendar = resourceType?.Elements().Any(e =>
                e.Name == CalDav + "calendar"
                || e.Name.LocalName.Equals("calendar", StringComparison.OrdinalIgnoreCase)) == true;
            if (!isCalendar)
                continue;

            var displayName = prop.Element(Dav + "displayname")?.Value?.Trim();
            var collectionId = CollectionIdFromHref(href);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = collectionId;

            var ownerHref = prop.Element(Dav + "owner")?.Element(Dav + "href")?.Value
                ?? prop.Descendants(Dav + "owner").Elements(Dav + "href").FirstOrDefault()?.Value;
            var isShared = DetectShared(href, ownerHref, accountUsername);

            results.Add(new CalendarCollectionInfo
            {
                DisplayName = displayName,
                Href = NormalizeUrl(href),
                CollectionId = collectionId,
                IsShared = isShared
            });
        }

        return results
            .GroupBy(c => c.Href, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Prefer DAV:prop from a 200 propstat that carries resourcetype (SOGo often emits a second 404 propstat).
    /// </summary>
    internal static XElement? SelectSuccessfulProp(XElement response)
    {
        XElement? fallback = null;
        foreach (var propstat in response.Elements(Dav + "propstat"))
        {
            var status = propstat.Element(Dav + "status")?.Value ?? "";
            if (!status.Contains("200", StringComparison.Ordinal))
                continue;
            var prop = propstat.Element(Dav + "prop");
            if (prop is null)
                continue;
            if (prop.Element(Dav + "resourcetype") is not null)
                return prop;
            fallback ??= prop;
        }

        // Last resort: any prop with resourcetype (namespace-agnostic status quirks)
        return fallback
               ?? response.Descendants(Dav + "prop")
                   .FirstOrDefault(p => p.Element(Dav + "resourcetype") is not null);
    }

    /// <summary>
    /// Shared/delegated when DAV:owner principal is not the authenticated user, or when the
    /// collection id uses SOGo's <c>_D_</c>/<c>_A_</c> email encoding for another user.
    /// </summary>
    internal static bool DetectShared(string collectionHref, string? ownerHref, string? accountUsername)
    {
        var accountEmail = ExtractEmail(accountUsername);
        var ownerEmail = ExtractEmail(ownerHref);

        if (!string.IsNullOrEmpty(ownerEmail) && !string.IsNullOrEmpty(accountEmail))
            return !string.Equals(ownerEmail, accountEmail, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(ownerEmail) && string.IsNullOrEmpty(accountEmail)
            && !string.IsNullOrWhiteSpace(accountUsername))
        {
            // Username may be a local-part only; treat as owned only if owner local-part matches exactly
            var ownerLocal = ownerEmail.Split('@')[0];
            if (string.Equals(ownerLocal, accountUsername.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ownerHref) && !string.IsNullOrWhiteSpace(accountUsername))
        {
            // Avoid naive substring matches (e.g. username "smith" matching alice.smith@…)
            if (OwnerMatchesAccount(ownerHref, accountUsername))
                return false;
            if (ownerHref.Contains('@', StringComparison.Ordinal) || ownerHref.Contains('/'))
                return true;
        }

        var id = CollectionIdFromHref(collectionHref);
        if (id.Contains('@', StringComparison.Ordinal))
            return true;

        // SOGo delegated id: alice_D_smith_A_example_D_com_personal → alice.smith@example.com + _personal
        if (TryDecodeSogoEncodedEmail(id, out var encodedEmail, out _)
            && !string.IsNullOrEmpty(accountEmail)
            && !string.Equals(encodedEmail, accountEmail, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>Decode SOGo path encoding: <c>_A_</c> → <c>@</c>, <c>_D_</c> → <c>.</c>.</summary>
    internal static bool TryDecodeSogoEncodedEmail(string collectionId, out string email, out string? calendarSuffix)
    {
        email = "";
        calendarSuffix = null;
        if (string.IsNullOrEmpty(collectionId) || !collectionId.Contains("_A_", StringComparison.Ordinal))
            return false;

        // Prefer splitting off a trailing _<calendarId> when present (e.g. _personal)
        var decoded = collectionId.Replace("_A_", "@", StringComparison.Ordinal)
            .Replace("_D_", ".", StringComparison.Ordinal);
        var at = decoded.IndexOf('@');
        if (at <= 0)
            return false;

        // Find end of domain: stop before a trailing _segment that looks like a calendar id
        // e.g. alice.smith@example.com_personal
        var afterAt = decoded[(at + 1)..];
        var underscore = afterAt.IndexOf('_');
        if (underscore > 0)
        {
            email = decoded[..(at + 1 + underscore)];
            calendarSuffix = afterAt[(underscore + 1)..];
            return email.Contains('@') && email.Contains('.');
        }

        email = decoded;
        return email.Contains('@');
    }

    internal static string? ExtractEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim();
        // Angle-addr
        var lt = v.LastIndexOf('<');
        var gt = v.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
            v = v[(lt + 1)..gt].Trim();

        // Path segment /SOGo/dav/user@domain/ → user@domain
        if (v.Contains('/'))
        {
            var parts = v.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts.Reverse())
            {
                if (part.Contains('@'))
                    return part.Trim();
            }
        }

        return v.Contains('@') ? v : null;
    }

    private static bool OwnerMatchesAccount(string ownerHref, string accountUsername)
    {
        var ownerEmail = ExtractEmail(ownerHref);
        var accountEmail = ExtractEmail(accountUsername);
        if (!string.IsNullOrEmpty(ownerEmail) && !string.IsNullOrEmpty(accountEmail))
            return string.Equals(ownerEmail, accountEmail, StringComparison.OrdinalIgnoreCase);

        // Exact path-segment equality only (not substring)
        var ownerId = CollectionIdFromHref(ownerHref.TrimEnd('/') + "/");
        return string.Equals(ownerId, accountUsername.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHref(string homeUrlWithSlash, string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return href;

        // Absolute path on same host
        if (href.StartsWith('/'))
        {
            var baseUri = new Uri(homeUrlWithSlash);
            return new Uri(baseUri, href).ToString();
        }

        return new Uri(new Uri(homeUrlWithSlash), href).ToString();
    }

    public static string BuildPropfindXml()
    {
        var query = new XElement(Dav + "propfind",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", CalDav.NamespaceName),
            new XElement(Dav + "prop",
                new XElement(Dav + "displayname"),
                new XElement(Dav + "resourcetype"),
                new XElement(Dav + "owner")));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + query.ToString(SaveOptions.OmitDuplicateNamespaces);
    }
}
