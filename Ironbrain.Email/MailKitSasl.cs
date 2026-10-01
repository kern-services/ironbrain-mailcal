namespace Ironbrain.Email;

/// <summary>
/// MailKit ranks SASL mechanisms by strength and prefers <c>SCRAM-*-PLUS</c> (TLS channel binding)
/// when the server advertises them. Some servers (observed: mox) advertise PLUS first; MailKit can
/// then hang inside <c>AuthenticateAsync</c> while AUTH LOGIN/PLAIN fails fast. Strip channel-binding
/// variants and, when LOGIN/PLAIN remain, drop SCRAM so auth uses mechanisms known to complete promptly.
/// </summary>
public static class MailKitSasl
{
    /// <summary>
    /// Mutates <paramref name="mechanisms"/> in place (MailKit's post-connect
    /// <c>AuthenticationMechanisms</c> set). Returns how many entries were removed.
    /// </summary>
    public static int PreferSafeAuthenticationMechanisms(ISet<string> mechanisms)
    {
        ArgumentNullException.ThrowIfNull(mechanisms);

        var removed = 0;

        // Always disable channel-binding PLUS variants (SCRAM-SHA-*-PLUS, etc.).
        foreach (var name in mechanisms
                     .Where(m => m.EndsWith("-PLUS", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (mechanisms.Remove(name))
                removed++;
        }

        // When LOGIN or PLAIN is available, prefer those over remaining SCRAM-* so MailKit
        // does not negotiate SCRAM first (mox advertises SCRAM-*-PLUS ahead of LOGIN).
        var hasLoginOrPlain = mechanisms.Any(m =>
            m.Equals("LOGIN", StringComparison.OrdinalIgnoreCase)
            || m.Equals("PLAIN", StringComparison.OrdinalIgnoreCase));

        if (hasLoginOrPlain)
        {
            foreach (var name in mechanisms
                         .Where(m => m.StartsWith("SCRAM-", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                if (mechanisms.Remove(name))
                    removed++;
            }
        }

        return removed;
    }
}
