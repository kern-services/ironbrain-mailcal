using Xunit;

namespace Ironbrain.Email.Tests;

public sealed class MailKitSaslTests
{
    [Fact]
    public void PreferSafeAuthenticationMechanisms_StripsPlusAndScram_WhenLoginAvailable()
    {
        var mechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SCRAM-SHA-256-PLUS",
            "SCRAM-SHA-1-PLUS",
            "SCRAM-SHA-256",
            "LOGIN",
            "PLAIN"
        };

        var removed = MailKitSasl.PreferSafeAuthenticationMechanisms(mechanisms);

        Assert.Equal(3, removed);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LOGIN", "PLAIN" }, mechanisms);
    }

    [Fact]
    public void PreferSafeAuthenticationMechanisms_KeepsNonPlusScram_WhenNoLoginOrPlain()
    {
        var mechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SCRAM-SHA-256-PLUS",
            "SCRAM-SHA-256"
        };

        var removed = MailKitSasl.PreferSafeAuthenticationMechanisms(mechanisms);

        Assert.Equal(1, removed);
        Assert.Equal(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SCRAM-SHA-256" }, mechanisms);
    }

    [Fact]
    public void PreferSafeAuthenticationMechanisms_NoOp_WhenAlreadySafe()
    {
        var mechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LOGIN", "PLAIN" };
        Assert.Equal(0, MailKitSasl.PreferSafeAuthenticationMechanisms(mechanisms));
        Assert.Equal(2, mechanisms.Count);
    }
}
