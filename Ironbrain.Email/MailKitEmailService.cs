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

        using var client = await ConnectImapAsync(imap, cancellationToken).ConfigureAwait(false);

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

        using var client = await ConnectImapAsync(imap, cancellationToken).ConfigureAwait(false);

        var inbox = await client.GetFolderAsync(imap.Mailbox, cancellationToken);
        await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var uid = ParseUid(id);
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

    public async Task<EmailMoveResult> MoveEmailAsync(
        string id,
        string destinationMailbox,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationMailbox))
            throw new ArgumentException("Destination mailbox is required.", nameof(destinationMailbox));

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);
        var sourceName = string.IsNullOrWhiteSpace(sourceMailbox) ? imap.Mailbox : sourceMailbox.Trim();
        var destName = destinationMailbox.Trim().TrimEnd('/', '.');
        var uid = ParseUid(id);

        using var client = await ConnectImapAsync(imap, cancellationToken).ConfigureAwait(false);
        var source = await client.GetFolderAsync(sourceName, cancellationToken);
        await source.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var destination = await GetOrCreateMailboxAsync(client, destName, cancellationToken).ConfigureAwait(false);
        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";

        // MailKit uses UID MOVE when supported; otherwise COPY + \Deleted + EXPUNGE.
        await source.MoveToAsync(uid, destination, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Moved message Uid={Uid} from {FromMailbox} to {ToMailbox} via {Method}",
            uid, sourceName, destination.FullName, method);

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        return new EmailMoveResult
        {
            Id = id,
            FromMailbox = sourceName,
            ToMailbox = destination.FullName,
            Method = method
        };
    }

    public async Task<EmailMoveResult> ArchiveEmailAsync(
        string id,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken).ConfigureAwait(false);

        var sourceName = string.IsNullOrWhiteSpace(sourceMailbox) ? imap.Mailbox : sourceMailbox.Trim();
        var uid = ParseUid(id);

        using var client = await ConnectImapAsync(imap, cancellationToken).ConfigureAwait(false);
        var source = await client.GetFolderAsync(sourceName, cancellationToken);
        await source.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        DateTimeOffset? messageDate = null;
        var summaries = await source.FetchAsync(
            new[] { uid },
            MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate,
            cancellationToken).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault();
        if (summary is null)
            throw new InvalidOperationException($"Message with UID {id} was not found in mailbox '{sourceName}'.");

        messageDate = summary.Envelope?.Date;
        if (messageDate is null || messageDate == DateTimeOffset.MinValue)
            messageDate = null; // fall back to UTC now per ArchiveFolderPath rules

        // Empty/unset ArchiveFolder → Archive/{CurrentYear} inside Expand
        var destPattern = ArchiveFolderPath.Expand(imap.ArchiveFolder, messageDate);
        var destination = await GetOrCreateMailboxAsync(client, destPattern, cancellationToken).ConfigureAwait(false);
        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";

        await source.MoveToAsync(uid, destination, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Archived message Uid={Uid} from {FromMailbox} to {ToMailbox} via {Method}",
            uid, sourceName, destination.FullName, method);

        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

        return new EmailMoveResult
        {
            Id = id,
            FromMailbox = sourceName,
            ToMailbox = destination.FullName,
            Method = method
        };
    }

    private async Task AppendToSentFolderAsync(ImapOptions imap, MimeMessage message, string sentFolderName, string to, string subject, CancellationToken cancellationToken)
    {
        try
        {
            using var imapClient = await ConnectImapAsync(imap, cancellationToken).ConfigureAwait(false);

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

    private static async Task<ImapClient> ConnectImapAsync(ImapOptions imap, CancellationToken cancellationToken)
    {
        var client = new ImapClient();
        client.ServerCertificateValidationCallback = (s, c, h, e) =>
        {
            if (e == SslPolicyErrors.None) return true;
            return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
        };
        await client.ConnectAsync(imap.Host, imap.Port, imap.UseSsl, cancellationToken).ConfigureAwait(false);
        await client.AuthenticateAsync(imap.Username, imap.Password, cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Resolves an existing mailbox or creates the hierarchy under the personal namespace when allowed.
    /// </summary>
    internal static async Task<IMailFolder> GetOrCreateMailboxAsync(ImapClient client, string mailboxPath, CancellationToken cancellationToken)
    {
        var normalized = mailboxPath.Trim().TrimEnd('/', '.', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Mailbox path must not be empty.", nameof(mailboxPath));

        try
        {
            return await client.GetFolderAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (FolderNotFoundException)
        {
            // create hierarchy below
        }

        if (client.PersonalNamespaces.Count == 0)
        {
            throw new InvalidOperationException(
                $"Mailbox '{normalized}' does not exist and cannot be created (no personal IMAP namespace). "
                + "Create the folder on the server, then retry.");
        }

        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var sep = root.DirectorySeparator;
        var pathForSplit = normalized.Replace('/', sep).Replace('\\', sep);
        var parts = pathForSplit.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException($"Invalid mailbox path '{mailboxPath}'.");

        IMailFolder current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            try
            {
                var existing = await current.GetSubfolderAsync(parts[i], cancellationToken).ConfigureAwait(false);
                current = existing ?? throw new FolderNotFoundException(parts[i]);
            }
            catch (FolderNotFoundException)
            {
                try
                {
                    // Mark as message folder so year/month leaves are selectable; parents may also hold mail.
                    var created = await current.CreateAsync(parts[i], isMessageFolder: true, cancellationToken)
                        .ConfigureAwait(false);
                    current = created ?? throw new InvalidOperationException(
                        $"Mailbox '{normalized}' create returned no folder at segment '{parts[i]}'.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Mailbox '{normalized}' does not exist and could not be created "
                        + $"(failed at segment '{parts[i]}'). Create it on the server or grant CREATE permission.",
                        ex);
                }
            }
        }

        return current;
    }

    public static UniqueId ParseUid(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !uint.TryParse(id.Trim(), out var value) || value == 0)
            throw new ArgumentException($"Invalid IMAP UID '{id}'. Expected a positive integer.", nameof(id));
        return new UniqueId(value);
    }
}
