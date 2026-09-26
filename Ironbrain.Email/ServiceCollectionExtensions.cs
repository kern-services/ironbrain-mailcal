using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ironbrain.Email;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<ImapOptions>(configuration.GetSection(ImapOptions.SectionName));
        services.AddScoped<IEmailService, MailKitEmailService>();
        return services;
    }
}


