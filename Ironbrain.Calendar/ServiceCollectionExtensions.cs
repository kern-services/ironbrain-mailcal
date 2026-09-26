using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ironbrain.Calendar;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCalendar(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CalendarOptions>(configuration.GetSection(CalendarOptions.SectionName));
        services.AddHttpClient(nameof(CalDavCalendarService));
        services.AddScoped<ICalendarService, CalDavCalendarService>();
        return services;
    }

    /// <summary>
    /// Registers calendar services using an already-bound <see cref="CalendarOptions"/> instance (e.g. CLI).
    /// </summary>
    public static IServiceCollection AddCalendar(this IServiceCollection services, CalendarOptions options)
    {
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddHttpClient(nameof(CalDavCalendarService));
        services.AddScoped<ICalendarService, CalDavCalendarService>();
        return services;
    }
}
