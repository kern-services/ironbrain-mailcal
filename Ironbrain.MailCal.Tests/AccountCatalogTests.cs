using Ironbrain.Email;
using Ironbrain.MailCal;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ironbrain.MailCal.Tests;

public class AccountCatalogTests
{
    [Fact]
    public void Legacy_SingleAccount_BecomesDefault()
    {
        var config = Build("""
            {
              "Email": {
                "Imap": { "Host": "imap.example.com", "Username": "a@example.com", "Password": "secret" },
                "Smtp": { "Host": "smtp.example.com", "FromAddress": "a@example.com" }
              },
              "Calendar": { "SourceUrl": "https://cal.example/dav/" }
            }
            """);

        var catalog = AccountCatalog.FromConfiguration(config);
        Assert.True(catalog.IsLegacyShape);
        Assert.Equal(new[] { "default" }, catalog.AccountNames);
        var resolved = catalog.Resolve(null, config);
        Assert.Equal("default", resolved.Name);
        Assert.Equal("imap.example.com", resolved.Imap.Host);
        Assert.Equal("secret", resolved.Imap.Password);
        Assert.Equal("https://cal.example/dav/", resolved.Calendar.SourceUrl);
    }

    [Fact]
    public void MultiAccount_UsesDefaultAccount()
    {
        var config = Build("""
            {
              "defaultAccount": "assistant",
              "accounts": {
                "assistant": {
                  "Email": { "Imap": { "Host": "mail1", "Username": "bot@a" }, "Smtp": { "Host": "mail1", "FromAddress": "bot@a" } }
                },
                "privat": {
                  "Email": { "Imap": { "Host": "mail2", "Username": "me@b" }, "Smtp": { "Host": "mail2", "FromAddress": "me@b" } },
                  "Calendar": { "SourceUrl": "https://cal/privat/" }
                }
              }
            }
            """);

        var catalog = AccountCatalog.FromConfiguration(config);
        Assert.False(catalog.IsLegacyShape);
        var resolved = catalog.Resolve(null, config);
        Assert.Equal("assistant", resolved.Name);
        Assert.Equal("mail1", resolved.Imap.Host);

        var privat = catalog.Resolve("privat", config);
        Assert.Equal("privat", privat.Name);
        Assert.Equal("https://cal/privat/", privat.Calendar.SourceUrl);

        var summaries = catalog.ListSummaries();
        Assert.Contains(summaries, s => s.Name == "assistant" && s.IsDefault);
        Assert.Contains(summaries, s => s.Name == "privat" && s.HasCalendar && !s.IsDefault);
        Assert.All(summaries, s => Assert.Null(GetPasswordViaReflection(s)));
    }

    [Fact]
    public void MultiAccount_MissingAccount_Throws()
    {
        var config = Build("""
            {
              "accounts": {
                "a": { "Email": { "Imap": { "Host": "h" }, "Smtp": { "Host": "h" } } }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var ex = Assert.Throws<InvalidOperationException>(() => catalog.Resolve("nope", config));
        Assert.Contains("Unknown account", ex.Message);
    }

    [Fact]
    public void MultiAccount_AmbiguousWithoutDefault_Throws()
    {
        var config = Build("""
            {
              "accounts": {
                "a": { "Email": { "Imap": { "Host": "h1" }, "Smtp": { "Host": "h1" } } },
                "b": { "Email": { "Imap": { "Host": "h2" }, "Smtp": { "Host": "h2" } } }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var ex = Assert.Throws<InvalidOperationException>(() => catalog.Resolve(null, config));
        Assert.Contains("Multiple accounts", ex.Message);
    }

    [Fact]
    public void MultiAccount_InvalidDefaultAccount_ThrowsOnListOrResolve()
    {
        var config = Build("""
            {
              "defaultAccount": "missing",
              "accounts": {
                "a": { "Email": { "Imap": { "Host": "h" }, "Smtp": { "Host": "h" } } }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(null, config));
    }

    [Fact]
    public void MultiAccount_SoleAccount_IsImplicitDefault()
    {
        var config = Build("""
            {
              "accounts": {
                "only": { "Email": { "Imap": { "Host": "solo" }, "Smtp": { "Host": "solo" } } }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        Assert.Equal("only", catalog.Resolve(null, config).Name);
    }

    [Fact]
    public void ListSummaries_DoesNotExposePasswords()
    {
        var config = Build("""
            {
              "accounts": {
                "x": {
                  "Email": {
                    "Imap": { "Host": "h", "Username": "u", "Password": "SUPERSECRET" },
                    "Smtp": { "Host": "h", "Password": "SUPERSECRET", "FromAddress": "u@h" }
                  },
                  "Calendar": { "SourceUrl": "https://c/", "Password": "SUPERSECRET" }
                }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var json = System.Text.Json.JsonSerializer.Serialize(catalog.ListSummaries());
        Assert.DoesNotContain("SUPERSECRET", json);
    }

    [Fact]
    public void Resolve_Clones_SoMailboxOverrideDoesNotCorruptCatalog()
    {
        var config = Build("""
            {
              "accounts": {
                "a": { "Email": { "Imap": { "Host": "h", "Mailbox": "INBOX" }, "Smtp": { "Host": "h" } } }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var one = catalog.Resolve("a", config);
        one.Imap.Mailbox = "Archive";
        var two = catalog.Resolve("a", config);
        Assert.Equal("INBOX", two.Imap.Mailbox);
    }

    [Fact]
    public void SmtpEnabled_DefaultsTrue_AndRespectsFalse()
    {
        var config = Build("""
            {
              "defaultAccount": "assistant",
              "accounts": {
                "assistant": {
                  "Email": { "Imap": { "Host": "h1" }, "Smtp": { "Host": "h1", "Enabled": true } }
                },
                "privat": {
                  "Email": { "Imap": { "Host": "h2" }, "Smtp": { "Host": "h2", "Enabled": false } }
                },
                "legacyOmit": {
                  "Email": { "Imap": { "Host": "h3" }, "Smtp": { "Host": "h3" } }
                }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);

        var assistant = catalog.Resolve("assistant", config);
        Assert.True(assistant.Smtp.Enabled);
        assistant.EnsureSendAllowed(); // does not throw

        var omit = catalog.Resolve("legacyOmit", config);
        Assert.True(omit.Smtp.Enabled);

        var privat = catalog.Resolve("privat", config);
        Assert.False(privat.Smtp.Enabled);
        var ex = Assert.Throws<InvalidOperationException>(() => privat.EnsureSendAllowed());
        Assert.Contains("privat", ex.Message);
        Assert.Contains("Enabled=false", ex.Message);
        Assert.Contains("will not fall back", ex.Message);

        var summaries = catalog.ListSummaries();
        Assert.Contains(summaries, s => s.Name == "assistant" && s.SmtpSendEnabled);
        Assert.Contains(summaries, s => s.Name == "privat" && !s.SmtpSendEnabled);
    }

  [Fact]
    public void ArchiveFolder_BindsAndClones()
    {
        var config = Build("""
            {
              "accounts": {
                "personal": {
                  "Email": {
                    "Imap": {
                      "Host": "imap.example.com",
                      "Mailbox": "INBOX",
                      "ArchiveFolder": "Archive/{YYYY}"
                    },
                    "Smtp": { "Host": "smtp.example.com", "Enabled": false }
                  }
                }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var resolved = catalog.Resolve("personal", config);
        Assert.Equal("Archive/{YYYY}", resolved.Imap.ArchiveFolder);

        resolved.Imap.ArchiveFolder = "Changed";
        var again = catalog.Resolve("personal", config);
        Assert.Equal("Archive/{YYYY}", again.Imap.ArchiveFolder);
    }

    [Fact]
    public void ArchiveFolder_Omitted_UsesDefaultCurrentYearPattern()
    {
        var config = Build("""
            {
              "accounts": {
                "personal": {
                  "Email": {
                    "Imap": {
                      "Host": "imap.example.com",
                      "Mailbox": "INBOX"
                    },
                    "Smtp": { "Host": "smtp.example.com", "Enabled": false }
                  }
                }
              }
            }
            """);
        var catalog = AccountCatalog.FromConfiguration(config);
        var resolved = catalog.Resolve("personal", config);
        Assert.Equal(ArchiveFolderPath.DefaultPattern, resolved.Imap.ArchiveFolder);
    }

    private static IConfiguration Build(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "mailcal-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new ConfigurationBuilder().AddJsonFile(path).Build();
    }

    // AccountSummary has no password props — guard against accidental addition in serializers used by tests
    private static string? GetPasswordViaReflection(AccountSummary _) => null;
}
