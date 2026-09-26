namespace Ironbrain.Email;

public interface IEmailService
{
    /// <param name="userId">When set, uses this user's mail config from the database; otherwise uses appsettings.</param>
    Task<IReadOnlyList<EmailSummary>> ListInboxAsync(int limit = 20, int offset = 0, string? userId = null, CancellationToken cancellationToken = default);

    Task<EmailContent> GetEmailAsync(string id, string? userId = null, CancellationToken cancellationToken = default);

    Task SendEmailAsync(EmailSendRequest request, string? userId = null, CancellationToken cancellationToken = default);
}


