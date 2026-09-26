using Ironbrain.Calendar;
using Ironbrain.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ironbrain.MailCal;

/// <summary>
/// Builds <see cref="IEmailService"/> / <see cref="ICalendarService"/> for a resolved account
/// without mutating global DI options (safe for concurrent MCP tool calls with different accounts).
/// </summary>
public sealed class MailCalClientFactory(IServiceProvider serviceProvider, IHttpClientFactory httpClientFactory, ILoggerFactory? loggerFactory = null)
{
    public (IEmailService Email, ICalendarService Calendar, ResolvedAccount Account) Create(ResolvedAccount account)
    {
        var logFactory = loggerFactory ?? NullLoggerFactory.Instance;
        var email = new MailKitEmailService(
            Options.Create(account.Smtp),
            Options.Create(account.Imap),
            serviceProvider,
            logFactory.CreateLogger<MailKitEmailService>());

        var calendar = new CalDavCalendarService(
            httpClientFactory,
            Options.Create(account.Calendar),
            serviceProvider,
            logFactory.CreateLogger<CalDavCalendarService>());

        return (email, calendar, account);
    }
}

public static class MailCalServiceCollectionExtensions
{
    public static IServiceCollection AddMailCal(this IServiceCollection services, AccountCatalog catalog)
    {
        services.AddSingleton(catalog);
        services.AddHttpClient(nameof(CalDavCalendarService));
        services.AddSingleton<MailCalClientFactory>();
        return services;
    }
}
