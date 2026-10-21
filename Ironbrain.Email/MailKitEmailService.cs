using System.Net.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;

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
                // Partial accounts are OK — fill the missing side from appsettings fallback.
                if (smtp is not null || imap is not null)
                    return (smtp ?? _smtpFallback, imap ?? _imapFallback);
            }
        }
        return (_smtpFallback, _imapFallback);
    }

    private async Task<(SmtpOptions Smtp, ImapOptions Imap)> GetSplitOptionsAsync(
        string? userId,
        CancellationToken cancellationToken,
        string? accountId,
        string? sourceAccountId,
        string? sendAccountId)
    {
        var sourceId = FirstNonEmpty(sourceAccountId, accountId);
        var sendId = FirstNonEmpty(sendAccountId, accountId);

        if (string.Equals(sourceId, sendId, StringComparison.OrdinalIgnoreCase)
            || (sourceId is null && sendId is null))
        {
            return await GetOptionsAsync(userId, cancellationToken, sourceId).ConfigureAwait(false);
        }

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, sourceId).ConfigureAwait(false);
        var (smtp, _) = await GetOptionsAsync(userId, cancellationToken, sendId).ConfigureAwait(false);
        return (smtp, imap);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    public async Task<EmailListResult> ListInboxAsync(
        int limit = 20,
        int offset = 0,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? mailbox = null,
        string? accountId = null,
        EmailListFilter? filter = null)
    {
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var clampedOffset = Math.Max(0, offset);

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);
        var folderName = ResolveMailbox(imap, mailbox);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);

        var inbox = await GetExistingMailboxAsync(client, folderName, timeoutCts.Token).ConfigureAwait(false);
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

        var fetchRequest = new FetchRequest(
            MessageSummaryItems.Envelope
            | MessageSummaryItems.UniqueId
            | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate);

        IReadOnlyList<EmailSummary> items;
        int totalCount;

        if (filter is { HasAny: true })
        {
            var query = BuildListSearchQuery(filter);
            var uids = await inbox.SearchAsync(query, timeoutCts.Token).ConfigureAwait(false);
            // Newest-first: higher UIDs are typically newer; fall back to InternalDate after fetch.
            var ordered = uids.OrderByDescending(u => u.Id).ToList();
            totalCount = ordered.Count;
            if (totalCount == 0 || clampedOffset >= totalCount)
            {
                items = [];
            }
            else
            {
                var pageUids = ordered.Skip(clampedOffset).Take(clampedLimit).ToList();
                var summaries = await inbox.FetchAsync(pageUids, fetchRequest, timeoutCts.Token)
                    .ConfigureAwait(false);
                items = summaries
                    .OrderByDescending(s => s.InternalDate)
                    .ThenByDescending(s => s.UniqueId.Id)
                    .Select(ToEmailSummary)
                    .ToList();
            }
        }
        else
        {
            totalCount = inbox.Count;
            var start = Math.Max(0, totalCount - clampedOffset - clampedLimit);
            var end = Math.Max(-1, totalCount - clampedOffset - 1);
            if (totalCount == 0 || end < 0 || start > end)
            {
                items = [];
            }
            else
            {
                var summaries = await inbox.FetchAsync(start, end, fetchRequest, timeoutCts.Token)
                    .ConfigureAwait(false);
                items = summaries
                    .OrderByDescending(s => s.InternalDate)
                    .ThenByDescending(s => s.UniqueId.Id)
                    .Select(ToEmailSummary)
                    .ToList();
            }
        }

        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

        int? nextOffset = null;
        if (clampedOffset + items.Count < totalCount)
            nextOffset = clampedOffset + items.Count;

        return new EmailListResult
        {
            Items = items,
            TotalCount = totalCount,
            Offset = clampedOffset,
            Limit = clampedLimit,
            NextOffset = nextOffset
        };
    }

    private static EmailSummary ToEmailSummary(IMessageSummary s) =>
        new()
        {
            Id = s.UniqueId.Id.ToString(),
            Subject = s.Envelope?.Subject ?? string.Empty,
            From = s.Envelope?.From?.FirstOrDefault()?.ToString() ?? string.Empty,
            Date = s.InternalDate ?? DateTimeOffset.MinValue,
            IsSeen = s.Flags.HasValue && s.Flags.Value.HasFlag(MessageFlags.Seen)
        };

    /// <summary>Builds an IMAP SEARCH query from list filters. Exposed for unit tests.</summary>
    internal static SearchQuery BuildListSearchQuery(EmailListFilter filter)
    {
        SearchQuery? query = null;

        void And(SearchQuery next) =>
            query = query is null ? next : query.And(next);

        if (filter.Unread == true)
            And(SearchQuery.NotSeen);
        else if (filter.Unread == false)
            And(SearchQuery.Seen);

        // IMAP SINCE = inclusive of that calendar day; BEFORE = exclusive of that calendar day.
        // Do not widen by ±1 day — docs/MCP/CLI advertise inclusive since / exclusive before.
        if (filter.Since is { } since)
            And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date));

        if (filter.Before is { } before)
            And(SearchQuery.DeliveredBefore(before.UtcDateTime.Date));

        if (!string.IsNullOrWhiteSpace(filter.FromContains))
            And(SearchQuery.FromContains(filter.FromContains.Trim()));

        if (!string.IsNullOrWhiteSpace(filter.SubjectContains))
            And(SearchQuery.SubjectContains(filter.SubjectContains.Trim()));

        return query ?? SearchQuery.All;
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

        var inbox = await GetExistingMailboxAsync(client, folderName, timeoutCts.Token).ConfigureAwait(false);
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

        MimeMessage message;
        try
        {
            message = await inbox.GetMessageAsync(uid, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (MessageNotFoundException)
        {
            await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);
            throw new EmailNotFoundException(id, folderName);
        }

        var content = new EmailContent
        {
            Id = id,
            Subject = message.Subject ?? string.Empty,
            From = message.From?.FirstOrDefault()?.ToString() ?? string.Empty,
            To = FormatAddressList(message.To),
            Cc = FormatAddressList(message.Cc),
            MessageId = string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId.Trim(),
            InReplyTo = FormatInReplyTo(message),
            References = FormatReferences(message),
            BodyText = message.TextBody ?? string.Empty,
            BodyHtml = message.HtmlBody,
            Date = message.Date,
            Attachments = CollectAttachmentInfos(message)
        };

        await client.DisconnectAsync(true, timeoutCts.Token);
        return content;
    }

    public async Task<EmailSaveDraftResult> SaveDraftAsync(
        EmailSaveDraftRequest request,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var fromAddress = ResolveDraftFromAddress(imap);
        MimeMessage? replySource = null;
        string? replySourceFolder = null;

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.ReplyToMessageId))
        {
            replySourceFolder = ResolveMailbox(imap, request.ReplyToMailbox);
            var uid = ParseUid(request.ReplyToMessageId);
            IMailFolder sourceFolder;
            try
            {
                sourceFolder = await GetExistingMailboxAsync(client, replySourceFolder, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (FolderNotFoundException ex)
            {
                throw new InvalidOperationException(
                    FormatFolderNotFound(replySourceFolder, imap, accountId, "SaveDraft reply source"),
                    ex);
            }

            await sourceFolder.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token).ConfigureAwait(false);
            replySource = await sourceFolder.GetMessageAsync(uid, timeoutCts.Token).ConfigureAwait(false);
        }

        var draftsFolderName = await ResolveDraftsFolderAsync(
                client, imap, request.DraftsFolder, timeoutCts.Token)
            .ConfigureAwait(false);

        IMailFolder draftsFolder;
        try
        {
            draftsFolder = await GetOrCreateMailboxAsync(client, draftsFolderName, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                FormatFolderNotFound(draftsFolderName, imap, accountId, "SaveDraft Drafts folder"),
                ex);
        }

        var message = await BuildDraftMimeAsync(request, fromAddress, replySource, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Saving draft: Folder={Folder}, From={From}, To={To}, Subject={Subject}, ReplyToUid={ReplyToUid}, Account={Account}",
            draftsFolderName,
            fromAddress,
            request.To ?? "(none)",
            message.Subject ?? "",
            request.ReplyToMessageId ?? "(none)",
            accountId ?? "(default)");

        var appendResult = await draftsFolder
            .AppendAsync(message, MessageFlags.Draft, timeoutCts.Token)
            .ConfigureAwait(false);

        var appendedUid = appendResult.HasValue ? appendResult.Value.Id.ToString() : string.Empty;
        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

        return new EmailSaveDraftResult
        {
            Id = appendedUid,
            Folder = draftsFolderName,
            From = fromAddress,
            Subject = message.Subject ?? string.Empty
        };
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
        AddAddresses(message.To, request.To);
        AddAddresses(message.Cc, request.Cc);
        AddAddresses(message.Bcc, request.Bcc);
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
        string? accountId = null,
        string? sourceAccountId = null,
        string? sendAccountId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.To))
            throw new ArgumentException("Recipient is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EmailId))
            throw new ArgumentException("Email id (IMAP UID) is required.", nameof(request));

        var effectiveSourceId = FirstNonEmpty(sourceAccountId, accountId);
        var effectiveSendId = FirstNonEmpty(sendAccountId, accountId);
        var (smtp, imap) = await GetSplitOptionsAsync(
                userId, cancellationToken, accountId, sourceAccountId, sendAccountId)
            .ConfigureAwait(false);
        EnsureSmtpEnabled(smtp);
        EnsureImapConfigured(imap);

        var folderName = ResolveMailbox(imap, request.SourceMailbox);
        var uid = ParseUid(request.EmailId);

        using var imapTimeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var imapClient = await ConnectImapAsync(imap, imapTimeoutCts.Token).ConfigureAwait(false);
        IMailFolder folder;
        try
        {
            folder = await GetExistingMailboxAsync(imapClient, folderName, imapTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (FolderNotFoundException ex)
        {
            throw new InvalidOperationException(
                FormatFolderNotFound(
                    folderName,
                    imap,
                    effectiveSourceId,
                    "ForwardEmail IMAP source"),
                ex);
        }

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
            "Forwarding email: Uid={Uid}, Mailbox={Mailbox}, To={To}, Subject={Subject}, Attachments={AttachmentCount}, From={From}, SourceAccount={SourceAccount}, SendAccount={SendAccount}, Config={ConfigSource}",
            request.EmailId, folderName, request.To, subject, attachmentCount, smtp.FromAddress,
            effectiveSourceId ?? "(default)", effectiveSendId ?? "(default)", configSource);

        // Sent-folder append uses send-side IMAP when available (same host as SMTP account).
        await SendMimeMessageAsync(smtp, imap, message, request.To.Trim(), subject, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string FormatFolderNotFound(
        string folderName,
        ImapOptions imap,
        string? accountId,
        string operation)
    {
        var account = string.IsNullOrWhiteSpace(accountId) ? "(default IMAP account)" : accountId.Trim();
        var user = string.IsNullOrWhiteSpace(imap.Username) ? "(no username)" : imap.Username.Trim();
        var host = string.IsNullOrWhiteSpace(imap.Host) ? "(no host)" : imap.Host.Trim();
        return
            $"{operation}: folder '{folderName}' not found on IMAP {user}@{host} " +
            $"(accountId={account}). " +
            "Pass sourceAccountId for the account that owns this mailbox " +
            "(same account as list_emails/get_email), or fix SourceMailbox.";
    }

    public async Task<EmailMoveResult> MoveEmailAsync(
        string id,
        string destinationMailbox,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        var bulk = await MoveEmailsAsync(
                [id],
                destinationMailbox,
                sourceMailbox,
                userId,
                cancellationToken,
                accountId)
            .ConfigureAwait(false);

        return new EmailMoveResult
        {
            Id = id,
            FromMailbox = bulk.FromMailbox,
            ToMailbox = bulk.ToMailbox,
            Method = bulk.Method
        };
    }

    public async Task<EmailBulkMoveResult> MoveEmailsAsync(
        IReadOnlyList<string> ids,
        string destinationMailbox,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(destinationMailbox))
            throw new ArgumentException("Destination mailbox is required.", nameof(destinationMailbox));

        var uids = ParseUids(ids);

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var sourceName = ResolveMailbox(imap, sourceMailbox);
        var destName = destinationMailbox.Trim().TrimEnd('/', '.');

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);
        var source = await GetExistingMailboxAsync(client, sourceName, timeoutCts.Token).ConfigureAwait(false);
        await source.OpenAsync(FolderAccess.ReadWrite, timeoutCts.Token);

        var destination = await GetOrCreateMailboxAsync(client, destName, timeoutCts.Token).ConfigureAwait(false);
        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";
        var uidMap = await source.MoveToAsync(uids, destination, timeoutCts.Token).ConfigureAwait(false);
        var moved = ResolveMovedSourceUids(uidMap, uids);

        logger.LogInformation(
            "Moved {Count} message(s) from {FromMailbox} to {ToMailbox} via {Method}",
            moved.Count, sourceName, destination.FullName, method);

        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);

        return new EmailBulkMoveResult
        {
            Ids = moved.Select(u => u.Id.ToString()).ToList(),
            FromMailbox = sourceName,
            ToMailbox = destination.FullName,
            Method = method,
            MovedCount = moved.Count
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
        var bulk = await ArchiveEmailsAsync(
                [id],
                sourceMailbox,
                userId,
                cancellationToken,
                messageDate,
                accountId)
            .ConfigureAwait(false);

        var first = bulk[0];
        return new EmailMoveResult
        {
            Id = id,
            FromMailbox = first.FromMailbox,
            ToMailbox = first.ToMailbox,
            Method = first.Method
        };
    }

    public async Task<IReadOnlyList<EmailBulkMoveResult>> ArchiveEmailsAsync(
        IReadOnlyList<string> ids,
        string? sourceMailbox = null,
        string? userId = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? messageDate = null,
        string? accountId = null)
    {
        var uids = ParseUids(ids);

        var (_, imap) = await GetOptionsAsync(userId, cancellationToken, accountId).ConfigureAwait(false);
        EnsureImapConfigured(imap);

        var sourceName = ResolveMailbox(imap, sourceMailbox);

        using var timeoutCts = CreateTimeoutCts(imap.TimeoutMs, cancellationToken);
        using var client = await ConnectImapAsync(imap, timeoutCts.Token).ConfigureAwait(false);
        var source = await GetExistingMailboxAsync(client, sourceName, timeoutCts.Token).ConfigureAwait(false);
        await source.OpenAsync(FolderAccess.ReadWrite, timeoutCts.Token);

        var method = client.Capabilities.HasFlag(ImapCapabilities.Move) ? "MOVE" : "COPY+DELETE";
        var groups = new Dictionary<string, List<UniqueId>>(StringComparer.Ordinal);

        if (messageDate is { } forced && forced != DateTimeOffset.MinValue)
        {
            var destPattern = ArchiveFolderPath.Expand(imap.ArchiveFolder, forced);
            groups[destPattern] = uids.ToList();
        }
        else
        {
            var summaries = await source.FetchAsync(
                    uids,
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            var byUid = summaries.ToDictionary(s => s.UniqueId);
            foreach (var uid in uids)
            {
                DateTimeOffset? effectiveDate = null;
                if (byUid.TryGetValue(uid, out var summary))
                {
                    effectiveDate = summary.Envelope?.Date;
                    if (effectiveDate is null || effectiveDate == DateTimeOffset.MinValue)
                        effectiveDate = summary.InternalDate;
                    if (effectiveDate is null || effectiveDate == DateTimeOffset.MinValue)
                        effectiveDate = null;
                }

                var destPattern = ArchiveFolderPath.Expand(imap.ArchiveFolder, effectiveDate);
                if (!groups.TryGetValue(destPattern, out var list))
                {
                    list = [];
                    groups[destPattern] = list;
                }

                list.Add(uid);
            }
        }

        var results = new List<EmailBulkMoveResult>(groups.Count);
        foreach (var (destPattern, groupUids) in groups)
        {
            var destination = await GetOrCreateMailboxAsync(client, destPattern, timeoutCts.Token)
                .ConfigureAwait(false);
            var uidMap = await source.MoveToAsync(groupUids, destination, timeoutCts.Token)
                .ConfigureAwait(false);
            var moved = ResolveMovedSourceUids(uidMap, groupUids);

            logger.LogInformation(
                "Archived {Count} message(s) from {FromMailbox} to {ToMailbox} via {Method}",
                moved.Count, sourceName, destination.FullName, method);

            results.Add(new EmailBulkMoveResult
            {
                Ids = moved.Select(u => u.Id.ToString()).ToList(),
                FromMailbox = sourceName,
                ToMailbox = destination.FullName,
                Method = method,
                MovedCount = moved.Count
            });
        }

        await client.DisconnectAsync(true, timeoutCts.Token).ConfigureAwait(false);
        return results;
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
        if (original.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
            || original.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase))
            return original;
        return string.IsNullOrEmpty(original) ? "Fwd:" : "Fwd: " + original;
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
    /// Opens an existing mailbox with separator-tolerant lookup (no create).
    /// Tries the path as given, then with <c>/</c>↔<c>.</c> alternates, then walks the
    /// personal namespace using the server directory separator.
    /// </summary>
    internal static async Task<IMailFolder> GetExistingMailboxAsync(
        ImapClient client,
        string mailboxPath,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeMailboxPath(mailboxPath);
        foreach (var candidate in FolderPathCandidates(normalized))
        {
            try
            {
                return await client.GetFolderAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (FolderNotFoundException)
            {
                // try next candidate
            }
        }

        if (client.PersonalNamespaces.Count > 0)
        {
            var walked = await TryWalkExistingMailboxAsync(client, normalized, cancellationToken)
                .ConfigureAwait(false);
            if (walked is not null)
                return walked;
        }

        throw new FolderNotFoundException(normalized);
    }

    internal static string NormalizeMailboxPath(string mailboxPath)
    {
        var normalized = mailboxPath.Trim().TrimEnd('/', '.', ' ');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Mailbox path must not be empty.", nameof(mailboxPath));
        return normalized;
    }

    /// <summary>Candidate full paths for separator-tolerant lookup (deterministic, no duplicates).</summary>
    internal static IReadOnlyList<string> FolderPathCandidates(string normalized)
    {
        var list = new List<string> { normalized };
        var slashToDot = normalized.Replace('/', '.');
        var dotToSlash = normalized.Replace('.', '/');
        if (!string.Equals(slashToDot, normalized, StringComparison.Ordinal))
            list.Add(slashToDot);
        if (!string.Equals(dotToSlash, normalized, StringComparison.Ordinal)
            && !string.Equals(dotToSlash, slashToDot, StringComparison.Ordinal))
            list.Add(dotToSlash);
        return list;
    }

    private static async Task<IMailFolder?> TryWalkExistingMailboxAsync(
        ImapClient client,
        string normalized,
        CancellationToken cancellationToken)
    {
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        var sep = root.DirectorySeparator;
        var pathForSplit = normalized.Replace('/', sep).Replace('\\', sep);
        // Also accept '.' as a separator when the server uses something else (common for Archive/2026 vs Archive.2026).
        if (sep != '.')
            pathForSplit = pathForSplit.Replace('.', sep);

        var parts = pathForSplit.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        IMailFolder current = root;
        foreach (var segment in parts)
        {
            if (segment is "." or ".." || segment.Contains("..", StringComparison.Ordinal))
                return null;
            try
            {
                var existing = await current.GetSubfolderAsync(segment, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                    return null;
                current = existing;
            }
            catch (FolderNotFoundException)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Resolves an existing mailbox or creates the hierarchy under the personal namespace when allowed.
    /// </summary>
    internal static async Task<IMailFolder> GetOrCreateMailboxAsync(ImapClient client, string mailboxPath, CancellationToken cancellationToken)
    {
        var normalized = NormalizeMailboxPath(mailboxPath);

        try
        {
            return await GetExistingMailboxAsync(client, normalized, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Parses and de-duplicates IMAP UIDs while preserving first-seen order.</summary>
    public static IList<UniqueId> ParseUids(IReadOnlyList<string> ids)
    {
        if (ids is null || ids.Count == 0)
            throw new ArgumentException("At least one IMAP UID is required.", nameof(ids));

        var result = new List<UniqueId>(ids.Count);
        var seen = new HashSet<uint>();
        foreach (var id in ids)
        {
            var uid = ParseUid(id);
            if (seen.Add(uid.Id))
                result.Add(uid);
        }

        if (result.Count == 0)
            throw new ArgumentException("At least one IMAP UID is required.", nameof(ids));

        return result;
    }

    /// <summary>
    /// Source UIDs actually moved per UIDPLUS mapping. When the server returns an empty map
    /// (no UIDPLUS), fall back to the requested set — we cannot observe partial success.
    /// </summary>
    internal static IReadOnlyList<UniqueId> ResolveMovedSourceUids(
        UniqueIdMap? uidMap,
        IList<UniqueId> requested)
    {
        if (uidMap is { Count: > 0 } && uidMap.Source is { Count: > 0 })
            return uidMap.Source.ToList();
        return requested.ToList();
    }

    /// <summary>
    /// API/display form: MimeKit MessageIdList tokens (no outer <c>&lt;&gt;</c>).
    /// Exchange <c>[id@…]</c> is returned as-is — wrapping in <c>&lt;&gt;</c> for clients
    /// would be display-only and must not change the MIME write path.
    /// </summary>
    internal static string? FormatInReplyTo(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            return NormalizeMessageId(message.InReplyTo);

        var raw = message.Headers[HeaderId.InReplyTo];
        return string.IsNullOrWhiteSpace(raw) ? null : NormalizeMessageId(raw);
    }

    /// <inheritdoc cref="FormatInReplyTo"/>
    internal static IReadOnlyList<string> FormatReferences(MimeMessage message)
    {
        if (message.References is { Count: > 0 })
        {
            return message.References
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(NormalizeMessageId)
                .ToList();
        }

        var raw = message.Headers[HeaderId.References];
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        // Space-separated Message-Ids, optionally angle-bracketed.
        // Bare Exchange [id] tokens without <> are not reliably parsed by MimeKit
        // (MessageIdList drops them on write) — prefer the References collection above.
        return raw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeMessageId)
            .Where(r => r.Length > 0)
            .ToList();
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

    /// <summary>
    /// Draft From is the IMAP account identity (username), never SMTP send-as FromAddress.
    /// </summary>
    internal static string ResolveDraftFromAddress(ImapOptions imap)
    {
        var user = imap.Username?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(user))
        {
            throw new InvalidOperationException(
                "IMAP username is required to set draft From (IMAP account identity, not SMTP send-as).");
        }

        return user;
    }

    /// <summary>
    /// Resolves Drafts folder: request override → config → SPECIAL-USE \Drafts → Drafts / Entwürfe.
    /// </summary>
    internal static async Task<string> ResolveDraftsFolderAsync(
        ImapClient client,
        ImapOptions imap,
        string? requestOverride,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestOverride))
            return NormalizeMailboxPath(requestOverride);

        if (!string.IsNullOrWhiteSpace(imap.DraftsFolder))
            return NormalizeMailboxPath(imap.DraftsFolder);

        try
        {
            var special = client.GetFolder(SpecialFolder.Drafts);
            if (special is not null && !string.IsNullOrWhiteSpace(special.FullName))
                return special.FullName;
        }
        catch (NotSupportedException)
        {
            // Server has no SPECIAL-USE \Drafts mapping.
        }
        catch (FolderNotFoundException)
        {
            // Mapped but missing — fall through to name candidates.
        }

        foreach (var candidate in DraftsFolderNameFallbacks)
        {
            try
            {
                var existing = await GetExistingMailboxAsync(client, candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.FullName))
                    return existing.FullName;
            }
            catch (FolderNotFoundException)
            {
                // try next
            }
        }

        return DraftsFolderNameFallbacks[0];
    }

    internal static readonly string[] DraftsFolderNameFallbacks = ["Drafts", "Entwürfe"];

    internal static async Task<MimeMessage> BuildDraftMimeAsync(
        EmailSaveDraftRequest request,
        string fromAddress,
        MimeMessage? replySource,
        CancellationToken cancellationToken)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(fromAddress));
        AddAddresses(message.To, request.To);
        AddAddresses(message.Cc, request.Cc);
        AddAddresses(message.Bcc, request.Bcc);

        var subject = request.Subject?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subject) && replySource is not null)
            subject = ResolveReplySubject(null, replySource.Subject);
        message.Subject = subject;

        if (replySource is not null)
            ApplyReplyHeaders(message, replySource);

        var textBody = request.TextBody;
        var htmlBody = request.HtmlBody;
        if (request.QuoteOriginal && replySource is not null)
        {
            // Pass the unquoted reply text into the HTML path so a text-only reply
            // becomes a real HTML part before the blockquote (not quote-only).
            htmlBody = AppendQuotedOriginalHtml(htmlBody, textBody, replySource);
            textBody = AppendQuotedOriginalText(textBody, replySource);
        }

        var builder = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody = htmlBody
        };

        if (request.Attachments is { Count: > 0 })
        {
            foreach (var attachment in request.Attachments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attachment.Content is null || attachment.Content.Length == 0)
                    continue;
                var fileName = string.IsNullOrWhiteSpace(attachment.FileName)
                    ? "attachment"
                    : attachment.FileName.Trim();
                ContentType? contentType = null;
                if (!string.IsNullOrWhiteSpace(attachment.ContentType)
                    && ContentType.TryParse(attachment.ContentType, out var parsed))
                {
                    contentType = parsed;
                }

                if (contentType is null)
                    builder.Attachments.Add(fileName, attachment.Content);
                else
                    builder.Attachments.Add(fileName, attachment.Content, contentType);
            }
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    internal static void ApplyReplyHeaders(MimeMessage draft, MimeMessage original)
    {
        // Prefer the raw header so we do not trigger MimeKit's MessageId auto-generation
        // when the source message never had a Message-ID.
        var rawId = original.Headers[HeaderId.MessageId];
        if (string.IsNullOrWhiteSpace(rawId))
            return;

        // Exchange / NuGet Message-Ids are often "[id@microsoft.com]" (MimeKit#912).
        // That bracketed token *is* MimeKit's MessageId — strip only outer RFC <…>,
        // never []. MimeKit's InReplyTo/References setters then write <[id@…]>, which
        // matches the original for threading.
        var originalId = NormalizeMessageId(rawId);
        draft.InReplyTo = originalId;

        var references = new List<string>();
        if (original.References is { Count: > 0 })
        {
            foreach (var r in original.References)
            {
                // Byte-for-byte copy of prior chain entries (including Exchange []).
                // Do not re-normalize or case-fold: MimeKit already stores MessageIdList tokens.
                if (!string.IsNullOrWhiteSpace(r))
                    references.Add(r);
            }
        }

        // Preserve prior tokens byte-for-byte, but treat Message-Ids as case-insensitive
        // for the append-if-missing check so we do not emit the same ID twice.
        if (!references.Exists(r => string.Equals(r, originalId, StringComparison.OrdinalIgnoreCase)))
            references.Add(originalId);

        draft.References.Clear();
        foreach (var r in references)
            draft.References.Add(r);
    }

    /// <summary>
    /// Strips a single outer RFC <c>&lt;…&gt;</c> pair for MimeKit property setters.
    /// Does <b>not</b> strip Exchange/Microsoft <c>[…]</c> — that form is the Message-Id
    /// (MimeKit <c>MessageId</c> equals <c>[id@…]</c>); removing it breaks threading.
    /// </summary>
    internal static string NormalizeMessageId(string messageId)
    {
        var id = messageId.Trim();
        if (id.StartsWith('<') && id.EndsWith('>') && id.Length >= 2)
            id = id[1..^1];
        return id;
    }

    internal static string ResolveReplySubject(string? requestedSubject, string? originalSubject)
    {
        if (!string.IsNullOrWhiteSpace(requestedSubject))
            return requestedSubject.Trim();

        var original = originalSubject?.Trim() ?? string.Empty;
        if (original.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
            return original;
        return string.IsNullOrEmpty(original) ? "Re:" : "Re: " + original;
    }

    internal static string AppendQuotedOriginalText(string? body, MimeMessage original)
    {
        var from = original.From?.ToString() ?? string.Empty;
        var header =
            Environment.NewLine + Environment.NewLine
            + $"On {original.Date:u}, {from} wrote:" + Environment.NewLine;
        var originalText = ResolveOriginalPlainText(original);
        var quoted = string.Join(
            Environment.NewLine,
            originalText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => "> " + line));
        return (body ?? string.Empty) + header + quoted;
    }

    /// <summary>
    /// Builds the HTML part for a quoted reply. When <paramref name="htmlBody"/> is empty,
    /// falls back to an HTML-encoded form of <paramref name="textBody"/> so the reply is not
    /// quote-only. Quotes only the original <c>&lt;body&gt;</c> inner HTML (never a full document).
    /// </summary>
    internal static string? AppendQuotedOriginalHtml(
        string? htmlBody,
        string? textBody,
        MimeMessage original)
    {
        if (string.IsNullOrWhiteSpace(htmlBody)
            && string.IsNullOrWhiteSpace(textBody)
            && string.IsNullOrWhiteSpace(original.HtmlBody)
            && string.IsNullOrWhiteSpace(original.TextBody))
            return htmlBody;

        var replyHtml = !string.IsNullOrWhiteSpace(htmlBody)
            ? htmlBody
            : (!string.IsNullOrWhiteSpace(textBody) ? PlainTextToHtml(textBody) : null);

        var from = System.Net.WebUtility.HtmlEncode(original.From?.ToString() ?? string.Empty);
        var quoteHeader = $"<p>On {original.Date:u}, {from} wrote:</p>";
        var quoteBody = !string.IsNullOrWhiteSpace(original.HtmlBody)
            ? ExtractHtmlBodyInner(original.HtmlBody)
            : "<pre>" + System.Net.WebUtility.HtmlEncode(original.TextBody ?? string.Empty) + "</pre>";
        var block = quoteHeader + "<blockquote type=\"cite\">" + quoteBody + "</blockquote>";
        return string.IsNullOrWhiteSpace(replyHtml) ? block : replyHtml + block;
    }

    /// <summary>HTML-encode plain text, map newlines to <c>&lt;br&gt;</c>, wrap in <c>&lt;div&gt;</c>.</summary>
    internal static string PlainTextToHtml(string text)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "<br>", StringComparison.Ordinal);
        return "<div>" + encoded + "</div>";
    }

    private static readonly TimeSpan HtmlRegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex BodyInnerRegex = new(
        @"<body\b[^>]*>(.*)</body>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
        | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex DoctypeRegex = new(
        @"<!DOCTYPE[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex HtmlTagRegex = new(
        @"</?html\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex HeadBlockRegex = new(
        @"<head\b[^>]*>.*?</head>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
        | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex BodyTagRegex = new(
        @"</?body\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex StyleBlockRegex = new(
        @"<style\b[^>]*>.*?</style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
        | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    private static readonly Regex VoidChromeRegex = new(
        @"<(meta|base|link)\b[^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled
        | RegexOptions.NonBacktracking,
        HtmlRegexTimeout);

    /// <summary>
    /// Returns the original message's plain text, converting HTML-only bodies via MimeKit
    /// <see cref="HtmlTokenizer"/> so text quotes are not empty.
    /// </summary>
    internal static string ResolveOriginalPlainText(MimeMessage original)
    {
        if (!string.IsNullOrWhiteSpace(original.TextBody))
            return original.TextBody;
        if (string.IsNullOrWhiteSpace(original.HtmlBody))
            return string.Empty;
        return HtmlToPlainText(original.HtmlBody);
    }

    /// <summary>
    /// Convert HTML to plain text via MimeKit <see cref="HtmlTokenizer"/> (MimeKit 4.x has no
    /// dedicated HtmlToText converter class). Skips text inside style/script/title/head.
    /// </summary>
    internal static string HtmlToPlainText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { DecodeCharacterReferences = true };
        var sb = new System.Text.StringBuilder();
        var skipDepth = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken skipTag
                && skipTag.Id is HtmlTagId.Style or HtmlTagId.Script or HtmlTagId.Title or HtmlTagId.Head)
            {
                if (skipTag.IsEndTag)
                {
                    if (skipDepth > 0)
                        skipDepth--;
                }
                else if (!skipTag.IsEmptyElement)
                {
                    skipDepth++;
                }

                continue;
            }

            if (skipDepth > 0)
                continue;

            switch (token)
            {
                case HtmlDataToken data:
                    sb.Append(data.Data);
                    break;
                case HtmlTagToken { Id: HtmlTagId.Br }:
                case HtmlTagToken { Id: HtmlTagId.HR }:
                    sb.AppendLine();
                    break;
                case HtmlTagToken { IsEndTag: true, Id: HtmlTagId.P or HtmlTagId.Div
                    or HtmlTagId.LI or HtmlTagId.TR or HtmlTagId.BlockQuote
                    or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3
                    or HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6 }:
                    sb.AppendLine();
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Prefer <c>&lt;body&gt;</c> inner HTML; otherwise strip doctype / html / head wrappers
    /// so a full document is never pasted inside a blockquote. Also strips meta/base/link/style
    /// chrome that may appear inside the body.
    /// </summary>
    internal static string ExtractHtmlBodyInner(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        string inner;
        var bodyMatch = BodyInnerRegex.Match(html);
        if (bodyMatch.Success)
        {
            inner = bodyMatch.Groups[1].Value;
        }
        else
        {
            var cleaned = DoctypeRegex.Replace(html, string.Empty);
            cleaned = HtmlTagRegex.Replace(cleaned, string.Empty);
            cleaned = HeadBlockRegex.Replace(cleaned, string.Empty);
            cleaned = BodyTagRegex.Replace(cleaned, string.Empty);
            inner = cleaned;
        }

        return StripBodyChrome(inner).Trim();
    }

    /// <summary>Remove meta/base/link/style elements that may appear inside body content.</summary>
    internal static string StripBodyChrome(string html)
    {
        var cleaned = StyleBlockRegex.Replace(html, string.Empty);
        return VoidChromeRegex.Replace(cleaned, string.Empty);
    }

    internal static string FormatAddressList(InternetAddressList? list)
    {
        if (list is null || list.Count == 0)
            return string.Empty;
        return string.Join(", ", list.Select(a => a.ToString()));
    }

    /// <summary>
    /// Parses comma/semicolon-separated address lists with MimeKit (same path as SMTP send).
    /// Exposed to tests via InternalsVisibleTo.
    /// </summary>
    internal static void AddAddresses(InternetAddressList target, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (MailboxAddress.TryParse(part, out var mailbox))
                target.Add(mailbox);
            else
                throw new ArgumentException($"Invalid email address '{part}'.", nameof(raw));
        }
    }

    /// <summary>Builds the outbound MIME for unit tests (From + To/Cc/Bcc + subject/body).</summary>
    internal static MimeMessage BuildSendMime(EmailSendRequest request, string fromAddress, string? fromName = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName ?? fromAddress, fromAddress));
        AddAddresses(message.To, request.To);
        AddAddresses(message.Cc, request.Cc);
        AddAddresses(message.Bcc, request.Bcc);
        message.Subject = request.Subject;
        message.Body = new BodyBuilder
        {
            TextBody = request.TextBody,
            HtmlBody = request.HtmlBody
        }.ToMessageBody();
        return message;
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
