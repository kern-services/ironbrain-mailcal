using Ironbrain.MailCal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Ironbrain.MailCal.Cli;

/// <summary>
/// Loads MailCal configuration from a JSON file and/or environment variables.
/// Secrets stay out of the repo: use a local config path or env overrides.
/// </summary>
internal static class ConfigLoader
{
    public const string DefaultConfigEnvVar = "IRONBRAIN_MAILCAL_CONFIG";

    public static IConfiguration BuildConfiguration(string? configPath)
    {
        var resolved = ResolveConfigPath(configPath);
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory());

        if (resolved is not null)
            builder.AddJsonFile(resolved, optional: false, reloadOnChange: false);
        else
            builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        builder
            .AddEnvironmentVariables(prefix: "IRONBRAIN_")
            .AddEnvironmentVariables(); // Email__Imap__Host and Accounts__name__Email__…

        return builder.Build();
    }

    public static string? ResolveConfigPath(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
            return Path.GetFullPath(configPath);

        var fromEnv = Environment.GetEnvironmentVariable(DefaultConfigEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".config", "ironbrain", "mailcal.json"),
            Path.Combine(home, ".ironbrain", "mailcal.json"),
            Path.GetFullPath("mailcal.json"),
            Path.GetFullPath("appsettings.json")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static (ServiceProvider Sp, AccountCatalog Catalog, IConfiguration Configuration) BuildServices(
        IConfiguration configuration,
        bool verbose)
    {
        var catalog = AccountCatalog.FromConfiguration(configuration);
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddConsole();
            b.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        });
        services.AddSingleton(configuration);
        services.AddMailCal(catalog);
        return (services.BuildServiceProvider(), catalog, configuration);
    }

    public static void WriteJson(object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }
}
