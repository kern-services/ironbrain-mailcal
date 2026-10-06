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

    private async Task<(SmtpOptions Smtp, ImapOptions Imap)> GetOptionsAsync(
        string? userId,
        CancellationToken cancellationToken,
        string? accountId = null)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            using var scope = serviceProvider.CreateScope();
            var provider = scope.ServiceProvider.GetService<IUserEmailOptionsProvider>();
            if (provider is not null)
            {
                var (smtp, imap) = await provider.GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
                if (smtp is not null && imap is not null)
                    return (smtp, imap);
            }
        }
        return (_smtpFallback, _imapFallback);
    }

    public async Task<IReadOnlyList<EmailSummary>> ListInboxAsync(
        int limit = 20,
        int offset = 0,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? mailbox = null,
        string? accountId = null)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);
        var folderName = ResolveMailbox(imap, mailbox);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);

        var inbox = await client.GetFolderAsync(folderName, timeoutCts.Token);
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

        var messageCount = inbox.Count;
        var start = Math.Max(0, messageCount - offset - limit);
        var end = Math.Max(-1, messageCount - offset - 1);
        if (messageCount == 0 || end < 0 || start > end)
        {
            await client.DisconnectAsync(true, timeoutCts.Token);
            return [];
        }

        var fetchRequest = new FetchRequest(MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate);
        var summaries = await inbox.FetchAsync(start, end, fetchRequest, timeoutCts.Token);
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

        await client.DisconnectAsync(true, timeoutCts.Token);
        return result;
    }

    public async Task<EmailContent> GetEmailAsync(
        string id,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? mailbox = null,
        string? accountId = null)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);
        var folderName = ResolveMailbox(imap, mailbox);
        var uid = ParseUid(id);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);

        var inbox = await client.GetFolderAsync(folderName, timeoutCts.Token);
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

        var message = await inbox.GetMessageAsync(uid, timeoutCts.Token);
        var content = new EmailContent
        {
            Id = id,
            Subject = message.Subject ?? string.Empty,
            From = message.From?.FirstOrDefault()?.ToString() ?? string.Empty,
            BodyText = message.TextBody ?? string.Empty,
            BodyHtml = message.HtmlBody,
            Date = message.Date,
            Attachments = CollectAttachmentInfos(message)
        };

        await client.DisconnectAsync(true, timeoutCts.Token);
        return content;
    }

    public async Task SendEmailAsync(
        EmailSendRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        var (smtp, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureSmtpEnabled(smtp);

        var configSource = string.IsNullOrWhiteSpace(userId) ? "fallback (appsettings)" : "user config";
        logger.LogInformation("Sending email: To={To}, Subject={Subject}, From={From}, Config={ConfigSource}",
            request.To, request.Subject, smtp.FromAddress, configSource);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(request.To));
        message.Subject = request.Subject;
        message.Body = new BodyBuilder
        {
            TextBody = request.TextBody,
            HtmlBody = request.HtmlBody
        }.ToMessageBody();

        await SendMimeMessageAsync(smtp, imap, message, request.To, request.Subject, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ForwardEmailAsync(
        EmailForwardRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.To))
            throw new ArgumentException("Recipient is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EmailId))
            throw new ArgumentException("Email id (IMAP UID) is required.", nameof(request));

        var (smtp, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureSmtpEnabled(smtp);
        EnsureImapConfigured(imap);

        var folderName = ResolveMailbox(imap, request.SourceMailbox);
        var uid = ParseUid(request.EmailId);

        using var imapTimeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var imapClient = await ConnectImapAsync(imap, imapTimeoutCts.Token).ConfigureAwait(false);
        var folder = await imapClient.GetFolderAsync(folderName, imapTimeoutCts.Token).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, imapTimeoutCts.Token).ConfigureAwait(false);
        var original = await folder.GetMessageAsync(uid, imapTimeoutCts.Token).ConfigureAwait(false);
        await imapClient.DisconnectAsync(true, imapTimeoutCts.Token).ConfigureAwait(false);

        var subject = ResolveForwardSubject(request.Subject, original.Subject);
        var builder = await BuildForwardBodyAsync(original, request.Note, cancellationToken).ConfigureAwait(false);
        var attachmentCount = builder.Attachments.Count;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(request.To.Trim()));
        message.Subject = subject;
        message.Body = builder.ToMessageBody();

        var configSource = string.IsNullOrWhiteSpace(userId) ? "fallback (appsettings)" : "user config";
        logger.LogInformation(
            "Forwarding email: Uid={Uid}, Mailbox={Mailbox}, To={To}, Subject={Subject}, Attachments={AttachmentCount}, From={From}, Config={ConfigSource}",
            request.EmailId, folderName, request.To, subject, attachmentCount, smtp.FromAddress, configSource);

        await SendMimeMessageAsync(smtp, imap, message, request.To.Trim(), subject, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<EmailMoveResult> MoveEmailAsync(
        string id,
        string destinationMailbox,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(destinationMailbox))
            throw new ArgumentException("Destination mailbox is required.", nameof(destinationMailbox));

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var sourceName = ResolveMailbox(imap, sourceMailbox);
        var destName = destinationMailbox.Trim().TrimEnd('/', '.');
        var uid = ParseUid(id);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);
        var source = await client.GetFolderAsync(sourceName, timeoutCts.Token);
        await source.OpenAsync(FolderAccess.ReadWrite, timeoutCts.Token);

        var destination = await GetOrCreateMailboxAsync(client, destName, timeoutCts.Token).ConfigureAwait(false);
        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";
        await source.MoveToAsync(uid, destination, timeoutCts.Token).ConfigureAwait(false);

        logger.LogInformation(
            "Moved message Uid={Uid} from {FromMailbox} to {ToMailbox} via {Method}",
            uid, sourceName, destination.FullName, method);

        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

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
        CancellationToken cancellationToken = default,
        DateTimeOffset? messageDate = null,
        string? accountId = null)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var sourceName = ResolveMailbox(imap, sourceMailbox);
        var uid = ParseUid(id);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);
        var source = await client.GetFolderAsync(sourceName, timeoutCts.Token);
        await source.OpenAsync(FolderAccess.ReadWrite, timeoutCts.Token);

        DateTimeOffset? effectiveDate = messageDate;
        if (effectiveDate is null || effectiveDate == DateTimeOffset.MinValue)
        {
            var summaries = await source.FetchAsync(
                new[] { uid },
                MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate,
                timeoutCts.Token).ConfigureAwait(false);
            var summary = summaries.FirstOrDefault()
                ?? throw new InvalidOperationException($"Message with UID {id} was not found in mailbox '{sourceName}'.");

            effectiveDate = summary.Envelope?.Date;
            if (effectiveDate is null || effectiveDate == DateTimeOffset.MinValue)
                effectiveDate = summary.InternalDate;
            if (effectiveDate is null || effectiveDate == DateTimeOffset.MinValue)
                effectiveDate = null; // ArchiveFolderPath falls back to UTC now
        }

        var destPattern = ArchiveFolderPath.Expand(imap.ArchiveFolder, effectiveDate);
        var destination = await GetOrCreateMailboxAsync(client, destPattern, timeoutCts.Token).ConfigureAwait(false);
        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";
        await source.MoveToAsync(uid, destination, timeoutCts.Token).ConfigureAwait(false);

        logger.LogInformation(
            "Archived message Uid={Uid} from {FromMailbox} to {ToMailbox} via {Method}",
            uid, sourceName, destination.FullName, method);

        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

        return new EmailMoveResult
        {
            Id = id,
            FromMailbox = sourceName,
            ToMailbox = destination.FullName,
            Method = method
        };
    }

    public async Task<EmailMoveResult> TrashEmailAsync(
        string id,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var destination = string.IsNullOrWhiteSpace(imap.TrashFolder)
            ? "Trash"
            : imap.TrashFolder.Trim().TrimEnd('/', '.');

        return await MoveEmailAsync(id, destination, sourceMailbox, userId, cancellationToken, accountId)
            .ConfigureAwait(false);
    }

    private async Task SendMimeMessageAsync(
        SmtpOptions smtp,
        ImapOptions imap,
        MimeMessage message,
        string to,
        string subject,
        CancellationToken cancellationToken)
    {
        var smtpTimeoutMs = NormalizeTimeoutMs(smtp.TimeoutMs);
        using var timeoutCts = CreateTimeoutCts(smtpTimeoutMs, cancellationToken);
        var ct = timeoutCts.Token;

        using var client = CreateSmtpClient(smtpTimeoutMs);
        var secureSocket = smtp.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : (smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        logger.LogDebug("Connecting to SMTP {Host}:{Port} (timeoutMs={TimeoutMs})", smtp.Host, smtp.Port, smtpTimeoutMs);

        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, secureSocket, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(smtp.Username))
            {
                PreferSafeSasl(client);
                await client.AuthenticateAsync(smtp.Username, smtp.Password, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            logger.LogInformation("Email sent successfully: To={To}, Subject={Subject}", to, subject);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "SMTP send timed out after {TimeoutMs}ms: To={To}, Subject={Subject}, Host={Host}:{Port}",
                smtpTimeoutMs, to, subject, smtp.Host, smtp.Port);
            throw new TimeoutException(
                $"SMTP send timed out after {smtpTimeoutMs}ms contacting {smtp.Host}:{smtp.Port}. "
                + "Check SMTP host/port/TLS settings for this mail integration.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send email: To={To}, Subject={Subject}", to, subject);
            throw;
        }

        var sentFolderName = imap.SentFolder ?? "Sent";
        if (!string.IsNullOrWhiteSpace(sentFolderName) && !string.IsNullOrWhiteSpace(imap.Host))
        {
            await AppendToSentFolderAsync(imap, message, sentFolderName, to, subject, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void EnsureSmtpEnabled(SmtpOptions smtp)
    {
        if (!smtp.Enabled)
        {
            throw new InvalidOperationException(
                "SMTP send is disabled for this mail configuration (Email:Smtp:Enabled=false). "
                + "Enable sending on this account or use a different account; no automatic fallback.");
        }
    }

    /// <summary>
    /// Builds a classic forward body: optional note, forwarded-message header, original text/html,
    /// and copies of original attachment parts (Content-Disposition: attachment).
    /// </summary>
    internal static async Task<BodyBuilder> BuildForwardBodyAsync(
        MimeMessage original,
        string? note,
        CancellationToken cancellationToken)
    {
        var builder = new BodyBuilder();
        var from = original.From?.ToString() ?? string.Empty;
        var header =
            "---------- Forwarded message ----------" + Environment.NewLine
            + $"From: {from}" + Environment.NewLine
            + $"Date: {original.Date:u}" + Environment.NewLine
            + $"Subject: {original.Subject ?? string.Empty}" + Environment.NewLine
            + Environment.NewLine;

        var noteBlock = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var originalText = original.TextBody ?? string.Empty;
        builder.TextBody = noteBlock is null
            ? header + originalText
            : noteBlock + Environment.NewLine + Environment.NewLine + header + originalText;

        if (!string.IsNullOrWhiteSpace(original.HtmlBody))
        {
            var htmlHeader =
                "<p>---------- Forwarded message ----------<br/>"
                + $"From: {System.Net.WebUtility.HtmlEncode(from)}<br/>"
                + $"Date: {original.Date:u}<br/>"
                + $"Subject: {System.Net.WebUtility.HtmlEncode(original.Subject ?? string.Empty)}</p>";
            var htmlNote = noteBlock is null
                ? ""
                : $"<p>{System.Net.WebUtility.HtmlEncode(noteBlock).Replace("\n", "<br/>", StringComparison.Ordinal)}</p>";
            builder.HtmlBody = htmlNote + htmlHeader + original.HtmlBody;
        }

        foreach (var attachment in original.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attachment is not MimePart part)
                continue;

            if (part.Content is null)
                continue;

            await using var ms = new MemoryStream();
            await part.Content.DecodeToAsync(ms, cancellationToken).ConfigureAwait(false);
            var bytes = ms.ToArray();
            var fileName = ResolveAttachmentFileName(part);
            builder.Attachments.Add(fileName, bytes, part.ContentType);
        }

        return builder;
    }

    internal static string ResolveForwardSubject(string? requestedSubject, string? originalSubject)
    {
        if (!string.IsNullOrWhiteSpace(requestedSubject))
            return requestedSubject.Trim();

        var original = originalSubject?.Trim() ?? string.Empty;
        if (original.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase)
            || original.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
            return original;
        return string.IsNullOrEmpty(original) ? "Fw:" : "Fw: " + original;
    }

    internal static IReadOnlyList<EmailAttachmentInfo> CollectAttachmentInfos(MimeMessage message)
    {
        var list = new List<EmailAttachmentInfo>();
        foreach (var attachment in message.Attachments)
        {
            if (attachment is not MimePart part)
                continue;

            long size = 0;
            try
            {
                if (part.Content?.Stream is { CanSeek: true } stream)
                    size = stream.Length;
                else if (part.ContentDisposition?.Size is long declared && declared >= 0)
                    size = declared;
            }
            catch
            {
                size = 0;
            }

            list.Add(new EmailAttachmentInfo
            {
                FileName = ResolveAttachmentFileName(part),
                ContentType = part.ContentType?.MimeType,
                Size = size
            });
        }

        return list;
    }

    internal static string ResolveAttachmentFileName(MimePart part)
    {
        if (!string.IsNullOrWhiteSpace(part.FileName))
            return part.FileName.Trim();
        if (!string.IsNullOrWhiteSpace(part.ContentDisposition?.FileName))
            return part.ContentDisposition!.FileName!.Trim();
        if (!string.IsNullOrWhiteSpace(part.ContentType?.Name))
            return part.ContentType.Name.Trim();
        return "attachment";
    }

    private async Task AppendToSentFolderAsync(
        ImapOptions imap,
        MimeMessage message,
        string sentFolderName,
        string to,
        string subject,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
            using var imapClient = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);
            var sentFolder = await imapClient.GetFolderAsync(sentFolderName, timeoutCts.Token);
            await sentFolder.AppendAsync(message, MessageFlags.Seen, timeoutCts.Token, null);
            await imapClient.DisconnectAsync(true, timeoutCts.Token);
            logger.LogDebug("Copy appended to Sent folder {SentFolder}: To={To}, Subject={Subject}", sentFolderName, to, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append copy to Sent folder {SentFolder}: To={To}, Subject={Subject}. Email was still sent.", sentFolderName, to, subject);
        }
    }

    private async Task<ImapClient> ConnectImapAsync(ImapOptions imap, CancellationToken cancellationToken)
    {
        var client = CreateImapClient(imap.TimeoutMs);
        await client.ConnectAsync(imap.Host, imap.Port, imap.UseSsl, cancellationToken).ConfigureAwait(false);
        PreferSafeSasl(client);
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
            var segment = parts[i];
            if (segment is "." or ".." || segment.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Invalid folder segment '{segment}'.");

            try
            {
                var existing = await current.GetSubfolderAsync(segment, cancellationToken).ConfigureAwait(false);
                current = existing ?? throw new FolderNotFoundException(segment);
            }
            catch (FolderNotFoundException)
            {
                try
                {
                    var created = await current.CreateAsync(segment, isMessageFolder: true, cancellationToken)
                        .ConfigureAwait(false);
                    current = created ?? throw new InvalidOperationException(
                        $"Mailbox '{normalized}' create returned no folder at segment '{segment}'.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Mailbox '{normalized}' does not exist and could not be created "
                        + $"(failed at segment '{segment}'). Create it on the server or grant CREATE permission.",
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

    public static int NormalizeTimeoutMs(int timeoutMs) =>
        timeoutMs > 0 ? timeoutMs : SmtpOptions.DefaultTimeoutMs;

    private static void EnsureImapConfigured(ImapOptions imap)
    {
        if (string.IsNullOrWhiteSpace(imap.Host))
        {
            throw new InvalidOperationException(
                "IMAP is not configured. Set Email:Imap in config, or provide a host mail integration.");
        }
    }

    private static string ResolveMailbox(ImapOptions imap, string? mailboxOverride)
    {
        if (!string.IsNullOrWhiteSpace(mailboxOverride))
            return mailboxOverride.Trim();
        return string.IsNullOrWhiteSpace(imap.Mailbox) ? "INBOX" : imap.Mailbox;
    }

    private void PreferSafeSasl(IMailService client)
    {
        var before = string.Join(',', client.AuthenticationMechanisms.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        var removed = MailKitSasl.PreferSafeAuthenticationMechanisms(client.AuthenticationMechanisms);
        if (removed == 0)
            return;

        var after = string.Join(',', client.AuthenticationMechanisms.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        logger.LogInformation(
            "Adjusted SASL mechanisms for mail auth (removed {RemovedCount} SCRAM/PLUS entries). Before={Before} After={After}",
            removed, before, after);
    }

    private static CancellationTokenSource CreateTimeoutCts(int timeoutMs, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(NormalizeTimeoutMs(timeoutMs));
        return cts;
    }

    private static SmtpClient CreateSmtpClient(int timeoutMs)
    {
        var client = new SmtpClient
        {
            Timeout = NormalizeTimeoutMs(timeoutMs),
            ServerCertificateValidationCallback = AcceptDevChainErrors
        };
        return client;
    }

    private static ImapClient CreateImapClient(int timeoutMs)
    {
        var client = new ImapClient
        {
            Timeout = NormalizeTimeoutMs(timeoutMs),
            ServerCertificateValidationCallback = AcceptDevChainErrors
        };
        return client;
    }

    private static bool AcceptDevChainErrors(
        object s,
        System.Security.Cryptography.X509Certificates.X509Certificate? c,
        System.Security.Cryptography.X509Certificates.X509Chain? h,
        SslPolicyErrors e)
    {
        if (e == SslPolicyErrors.None) return true;
        return (e & SslPolicyErrors.RemoteCertificateChainErrors) != 0;
    }
}
