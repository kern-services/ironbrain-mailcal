using System.Net.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Ironbrain.Email;

public sealed class MailKitEmailService(
    IOptions<SmtpOptions> smtpOptions,
    IOptions<ImapOptions> imapOptions,
    IServiceProvider serviceProvider,
    ILogger<MailKitEmailService> logger) : IEmailService
{
    private readonly SmtpOptions _smtpFallback = smtpOptions.Value;
    private readonly ImapOptions _imapFallback = imapOptions.Value;

    private async Task<(SmtpOptions Smtp, ImapOptions Imap)> GetOptionsAsync(string? userId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            using var scope = serviceProvider.CreateScope();
            var provider = scope.ServiceProvider.GetService<IUserEmailOptionsProvider>();
            if (provider is not null)
            {
                var (smtp, imap) = await provider.GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);
                if (smtp is not null && imap is not null)
                    return (smtp, imap);
            }
        }
        return (_smtpFallback, _imapFallback);
    }

    public async Task<IReadOnlyList<EmailSummary>> ListInboxAsync(int limit = 20, int offset = 0, string? userId = null, CancellationToken cancellationToken = default)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);

        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) =>
        {
            if (e == SslPolicyErrors.None) return true;
            return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
        };
        await client.ConnectAsync(imap.Host, imap.Port, imap.UseSsl, cancellationToken);
        await client.AuthenticateAsync(imap.Username, imap.Password, cancellationToken);

        var inbox = await client.GetFolderAsync(imap.Mailbox, cancellationToken);
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var messageCount = inbox.Count;
        var start = Math.Max(0, messageCount - offset - limit);
        var end = Math.Max(-1, messageCount - offset - 1);
        if (messageCount == 0 || end < 0)
        {
            await client.DisconnectAsync(true, cancellationToken);
            return [];
        }

        if (start > end)
        {
            await client.DisconnectAsync(true, cancellationToken);
            return [];
        }

        var fetchRequest = new FetchRequest(MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate);
        var summaries = await inbox.FetchAsync(start, end, fetchRequest, cancellationToken);
        var result = summaries
            .OrderByDescending(s => s.InternalDate)
            .Select(s => new EmailSummary
            {
                Id = s.UniqueId.Id.ToString(),
                Subject = s.Envelope?.Subject ?? string.Empty,
                From = s.Envelope?.From?.FirstOrDefault()?.ToString() ?? string.Empty,
                Date = s.InternalDate ?? DateTimeOffset.MinValue,
                IsSeen = s.Flags.HasValue && s.Flags.Value.HasFlag(MessageFlags.Seen)
            })
            .ToList();

        await client.DisconnectAsync(true, cancellationToken);
        return result;
    }

    public async Task<EmailContent> GetEmailAsync(string id, string? userId = null, CancellationToken cancellationToken = default)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);

        using var client = new ImapClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) =>
        {
            if (e == SslPolicyErrors.None) return true;
            return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
        };
        await client.ConnectAsync(imap.Host, imap.Port, imap.UseSsl, cancellationToken);
        await client.AuthenticateAsync(imap.Username, imap.Password, cancellationToken);

        var inbox = await client.GetFolderAsync(imap.Mailbox, cancellationToken);
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var uid = new UniqueId(uint.Parse(id));
        var message = await inbox.GetMessageAsync(uid, cancellationToken);

        var textBody = message.TextBody ?? string.Empty;
        var htmlBody = message.HtmlBody;
        var content = new EmailContent
        {
            Id = id,
            Subject = message.Subject ?? string.Empty,
            From = message.From?.FirstOrDefault()?.ToString() ?? string.Empty,
            BodyText = textBody,
            BodyHtml = htmlBody,
            Date = message.Date
        };

        await client.DisconnectAsync(true, cancellationToken);
        return content;
    }

    public async Task SendEmailAsync(EmailSendRequest request, string? userId = null, CancellationToken cancellationToken = default)
    {
        var (smtp, imap) = await GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!smtp.Enabled)
        {
            throw new InvalidOperationException(
                "SMTP send is disabled for this mail configuration (Email:Smtp:Enabled=false). "
                + "Enable sending on this account or use a different account; no automatic fallback.");
        }

        var configSource = string.IsNullOrWhiteSpace(userId) ? "fallback (appsettings)" : "user config";
        logger.LogInformation("Sending email: To={To}, Subject={Subject}, From={From}, Config={ConfigSource}",
            request.To, request.Subject, smtp.FromAddress, configSource);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(request.To));
        message.Subject = request.Subject;

        var bodyBuilder = new BodyBuilder
        {
            TextBody = request.TextBody,
            HtmlBody = request.HtmlBody
        };
        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) =>
        {
            if (e == SslPolicyErrors.None) return true;
            return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
        };
        var secureSocket = smtp.UseSsl ? SecureSocketOptions.SslOnConnect : (smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        logger.LogDebug("Connecting to SMTP {Host}:{Port}", smtp.Host, smtp.Port);
        await client.ConnectAsync(smtp.Host, smtp.Port, secureSocket, cancellationToken);

        if (!string.IsNullOrWhiteSpace(smtp.Username))
        {
            await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
        }

        try
        {
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            logger.LogInformation("Email sent successfully: To={To}, Subject={Subject}", request.To, request.Subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email: To={To}, Subject={Subject}", request.To, request.Subject);
            throw;
        }

        var sentFolderName = imap.SentFolder ?? "Sent";
        if (!string.IsNullOrWhiteSpace(sentFolderName))
        {
            await AppendToSentFolderAsync(imap, message, sentFolderName, request.To, request.Subject, cancellationToken);
        }
    }

    private async Task AppendToSentFolderAsync(ImapOptions imap, MimeMessage message, string sentFolderName, string to, string subject, CancellationToken cancellationToken)
    {
        try
        {
            using var imapClient = new ImapClient();
            imapClient.ServerCertificateValidationCallback = (s, c, h, e) =>
            {
                if (e == SslPolicyErrors.None) return true;
                return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
            };
            await imapClient.ConnectAsync(imap.Host, imap.Port, imap.UseSsl, cancellationToken);
            await imapClient.AuthenticateAsync(imap.Username, imap.Password, cancellationToken);

            var sentFolder = await imapClient.GetFolderAsync(sentFolderName, cancellationToken);
            await sentFolder.AppendAsync(message, MessageFlags.Seen, cancellationToken, null);
            await imapClient.DisconnectAsync(true, cancellationToken);
            logger.LogDebug("Copy appended to Sent folder {SentFolder}: To={To}, Subject={Subject}", sentFolderName, to, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append copy to Sent folder {SentFolder}: To={To}, Subject={Subject}. Email was still sent.", sentFolderName, to, subject);
        }
    }
}
