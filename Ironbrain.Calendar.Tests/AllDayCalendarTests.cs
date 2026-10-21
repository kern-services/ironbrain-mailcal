using System.Net;
using System.Text;
using Ironbrain.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ironbrain.Calendar.Tests;

public class AllDayCalendarTests
{
    [Fact]
    public void FormatRangeForMessage_AllDaySingleDay()
    {
        var start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-29 (all-day)", CalDavCalendarService.FormatRangeForMessage(start, end, allDay: true));
    }

    [Fact]
    public void FormatRangeForMessage_AllDayMultiDay_ShowsInclusiveEnd()
    {
        // Exclusive DTEND 2026-11-02 → inclusive last day 2026-11-01
        var start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(
            "2026-10-29 – 2026-11-01 (all-day)",
            CalDavCalendarService.FormatRangeForMessage(start, end, allDay: true));
    }

    [Fact]
    public void FormatRangeForMessage_TimedMultiDay_KeepsBothDates()
    {
        var start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(
            "2026-10-29 00:00 – 2026-11-02 00:00",
            CalDavCalendarService.FormatRangeForMessage(start, end, allDay: false));
    }

    [Fact]
    public void ToAllDayCalDateTime_HasNoTime()
    {
        var cal = CalDavCalendarService.ToAllDayCalDateTime(new DateTime(2026, 10, 29, 15, 30, 0));
        Assert.False(cal.HasTime);
        Assert.Equal(new DateOnly(2026, 10, 29), DateOnly.FromDateTime(cal.Value));
    }

    [Fact]
    public async Task List_ParsesValueDate_AllDaySingleAndMulti()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:allday-one@ironbrain
            DTSTART;VALUE=DATE:20261029
            DTEND;VALUE=DATE:20261030
            SUMMARY:One day
            END:VEVENT
            BEGIN:VEVENT
            UID:allday-multi@ironbrain
            DTSTART;VALUE=DATE:20261029
            DTEND;VALUE=DATE:20261102
            SUMMARY:Multi day
            END:VEVENT
            END:VCALENDAR
            """;

        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(ics);
            ctx.Response.ContentType = "text/calendar";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            var sut = CreateSut(prefix + "calendar.ics", preferIcs: true);
            var items = await sut.GetAppointmentsInRangeAsync(
                new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc));

            var one = Assert.Single(items, a => a.Uid == "allday-one@ironbrain");
            Assert.True(one.AllDay);
            Assert.Equal(new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), one.Start);
            Assert.Equal(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), one.End);

            var multi = Assert.Single(items, a => a.Uid == "allday-multi@ironbrain");
            Assert.True(multi.AllDay);
            Assert.Equal(new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), multi.Start);
            Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), multi.End);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task AddAppointment_AllDay_WritesValueDate_ExclusiveEnd()
    {
        string? putBody = null;
        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var cts = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                if (ctx.Request.HttpMethod == "PROPFIND")
                {
                    var propfind = $$"""
                        <?xml version="1.0"?>
                        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/">
                          <d:response>
                            <d:href>/Calendar/personal/</d:href>
                            <d:propstat>
                              <d:prop>
                                <d:displayname>Personal</d:displayname>
                                <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                                <cs:getctag>1</cs:getctag>
                              </d:prop>
                              <d:status>HTTP/1.1 200 OK</d:status>
                            </d:propstat>
                          </d:response>
                        </d:multistatus>
                        """;
                    var bytes = Encoding.UTF8.GetBytes(propfind);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    putBody = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 201;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }

                ctx.Response.Close();
            }
        }, cts.Token);

        try
        {
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "Calendar/",
                Username = "u",
                Password = "p",
                DefaultWriteCalendar = "personal"
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var sut = new CalDavCalendarService(
                sp.GetRequiredService<IHttpClientFactory>(),
                options,
                sp,
                NullLogger<CalDavCalendarService>.Instance);

            var msg = await sut.AddAppointmentAsync(new CalendarAddRequest
            {
                Summary = "Urlaub",
                Start = new DateTime(2026, 10, 29),
                End = new DateTime(2026, 11, 2), // exclusive
                AllDay = true,
                CalendarSelector = "personal"
            });

            Assert.NotNull(putBody);
            Assert.Contains("DTSTART;VALUE=DATE:20261029", putBody, StringComparison.Ordinal);
            Assert.Contains("DTEND;VALUE=DATE:20261102", putBody, StringComparison.Ordinal);
            Assert.DoesNotContain("DTSTART;TZID", putBody, StringComparison.Ordinal);
            Assert.Contains("2026-10-29 – 2026-11-01 (all-day)", msg, StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* listener stopped */ }
        }
    }

    [Fact]
    public async Task AddAppointment_AllDay_RoundTripList()
    {
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        using var cts = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (ctx.Request.HttpMethod == "PROPFIND")
                {
                    var propfind = """
                        <?xml version="1.0"?>
                        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/">
                          <d:response>
                            <d:href>/Calendar/personal/</d:href>
                            <d:propstat>
                              <d:prop>
                                <d:displayname>Personal</d:displayname>
                                <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                                <cs:getctag>1</cs:getctag>
                              </d:prop>
                              <d:status>HTTP/1.1 200 OK</d:status>
                            </d:propstat>
                          </d:response>
                        </d:multistatus>
                        """;
                    WriteXml(ctx, 207, propfind);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    stored[path] = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 201;
                    ctx.Response.Close();
                }
                else if (ctx.Request.HttpMethod == "REPORT")
                {
                    var sb = new StringBuilder();
                    sb.Append("""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">""");
                    foreach (var (href, ics) in stored)
                    {
                        sb.Append("<d:response><d:href>")
                            .Append(System.Security.SecurityElement.Escape(href))
                            .Append("</d:href><d:propstat><d:prop><d:getetag>\"e1\"</d:getetag><c:calendar-data>")
                            .Append(System.Security.SecurityElement.Escape(ics))
                            .Append("</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
                    }
                    sb.Append("</d:multistatus>");
                    WriteXml(ctx, 207, sb.ToString());
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        }, cts.Token);

        try
        {
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "Calendar/",
                Username = "u",
                Password = "p",
                DefaultWriteCalendar = "personal"
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var sut = new CalDavCalendarService(
                sp.GetRequiredService<IHttpClientFactory>(),
                options,
                sp,
                NullLogger<CalDavCalendarService>.Instance);

            await sut.AddAppointmentAsync(new CalendarAddRequest
            {
                Summary = "Roundtrip",
                Start = new DateTime(2026, 10, 29),
                End = new DateTime(2026, 10, 30),
                AllDay = true,
                CalendarSelector = "personal"
            });

            var items = await sut.GetAppointmentsInRangeAsync(
                new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
                new CalendarQueryOptions { CalendarFilters = ["personal"] });

            var hit = Assert.Single(items, a => a.Summary == "Roundtrip");
            Assert.True(hit.AllDay);
            Assert.Equal(new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc), hit.Start);
            Assert.Equal(new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), hit.End);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* listener stopped */ }
        }
    }

    private static CalDavCalendarService CreateSut(string sourceUrl, bool preferIcs)
    {
        var options = Options.Create(new CalendarOptions
        {
            SourceUrl = sourceUrl,
            PreferIcsFeed = preferIcs
        });
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(CalDavCalendarService));
        var sp = services.BuildServiceProvider();
        return new CalDavCalendarService(
            sp.GetRequiredService<IHttpClientFactory>(),
            options,
            sp,
            NullLogger<CalDavCalendarService>.Instance);
    }

    private static void WriteXml(HttpListenerContext ctx, int status, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/xml";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
