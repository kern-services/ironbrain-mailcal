using System.Net;
using System.Net.Sockets;
using Ironbrain.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ironbrain.Email.Tests;

/// <summary>
/// Proves SMTP send fails within the configured timeout when the peer accepts TCP but never speaks SMTP.
/// </summary>
public sealed class MailKitEmailServiceTimeoutTests
{
    [Fact]
    public async Task SendEmailAsync_SilentSmtpPeer_FailsWithinTimeoutBudget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var acceptLoop = AcceptAndHoldAsync(listener, acceptCts.Token);

        const int timeoutMs = 2_000;
        var smtp = Options.Create(new SmtpOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = port,
            UseStartTls = false,
            UseSsl = false,
            FromAddress = "from@example.com",
            FromName = "Test",
            TimeoutMs = timeoutMs
        });
        var imap = Options.Create(new ImapOptions { Host = "", TimeoutMs = timeoutMs });

        var sut = new MailKitEmailService(
            smtp,
            imap,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<MailKitEmailService>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.SendEmailAsync(new EmailSendRequest
            {
                To = "to@example.com",
                Subject = "timeout probe",
                TextBody = "body"
            }));
        sw.Stop();

        acceptCts.Cancel();
        try { await acceptLoop; } catch { /* ignore */ }

        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected fail-fast within 15s, took {sw.Elapsed.TotalSeconds:F1}s ({ex.GetType().Name}: {ex.Message})");

        Assert.True(
            ex is TimeoutException
            || ex is OperationCanceledException
            || ex is IOException
            || ex.GetType().Name.Contains("Ssl", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || ex.InnerException is TimeoutException or OperationCanceledException,
            $"Unexpected exception type for silent peer: {ex.GetType().FullName}: {ex.Message}");
    }

    [Fact]
    public void NormalizeTimeoutMs_UsesDefaultWhenNonPositive()
    {
        Assert.Equal(SmtpOptions.DefaultTimeoutMs, MailKitEmailService.NormalizeTimeoutMs(0));
        Assert.Equal(SmtpOptions.DefaultTimeoutMs, MailKitEmailService.NormalizeTimeoutMs(-1));
        Assert.Equal(3_000, MailKitEmailService.NormalizeTimeoutMs(3_000));
    }

    private static async Task AcceptAndHoldAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var held = new List<TcpClient>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                held.Add(client);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on teardown
        }
        finally
        {
            foreach (var c in held)
                c.Dispose();
        }
    }
}
