namespace Ironbrain.Calendar;

/// <summary>
/// Resolves calendar options for a user (e.g. from user_configurations in the database).
/// Implemented by the host (e.g. API); when not registered, services fall back to appsettings / file config.
/// </summary>
public interface IUserCalendarOptionsProvider
{
    Task<CalendarOptions?> GetOptionsAsync(string? userId, CancellationToken cancellationToken = default);
}
