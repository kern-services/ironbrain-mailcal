using System.Net;
using System.Text;
using Ironbrain.Calendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ironbrain.Calendar.Tests;

public class CalDavCalendarServiceTests
{
    [Theory]
    [InlineData("today")]
    [InlineData("2026-09-25")]
    public void ResolveDayRange_AcceptsTodayAndIsoDate(string date)
    {
        var (start, end) = CalDavCalendarService.ResolveDayRange(date);
        Assert.Equal(1, (end - start).TotalDays, 0);
    }

    [Fact]
    public void ToCalDateTime_AcceptsLocalKind()
    {
        var local = DateTime.SpecifyKind(new DateTime(2026, 9, 25, 0, 0, 0), DateTimeKind.Local);
        var cal = CalDavCalendarService.ToCalDateTime(local);
        Assert.NotNull(cal);
        Assert.Equal(2026, cal.Year);
        Assert.Equal(9, cal.Month);
        Assert.Equal(25, cal.Day);
    }

    [Fact]
    public void ResolveDayRange_Today_IsUnspecifiedKind()
    {
        var (start, end) = CalDavCalendarService.ResolveDayRange("today");
        Assert.Equal(DateTimeKind.Unspecified, start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, end.Kind);
    }

    [Fact]
    public async Task GetAppointments_WithLocalKindRange_DoesNotThrow()
    {
        // Simulates pre-normalization Local "today" bounds (the SOGo smoke-test failure mode).
        var todayLocal = DateTime.Today; // Local
        var icsDate = todayLocal.ToString("yyyyMMdd");
        var ics = $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:local-kind@ironbrain
            DTSTART:{icsDate}T100000Z
            DTEND:{icsDate}T110000Z
            SUMMARY:Local kind range
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
            var options = Options.Create(new CalendarOptions
            {
                SourceUrl = prefix + "calendar.ics",
                PreferIcsFeed = true
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var sut = new CalDavCalendarService(factory, options, sp, NullLogger<CalDavCalendarService>.Instance);

            // Force Local Kind range (as DateTime.Today used to provide)
            var items = await sut.GetAppointmentsInRangeAsync(todayLocal, todayLocal.AddDays(1));
            Assert.Contains(items, a => a.Summary == "Local kind range");
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public void ParseDateTime_WithOffset_ReturnsUtc()
    {
        var dt = CalDavCalendarService.ParseDateTime("2026-09-25T14:00:00+02:00");
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(12, dt.Hour);
    }

    [Fact]
    public async Task GetAppointments_FromIcsFeed_FiltersByDay()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Ironbrain//Test//EN
            BEGIN:VEVENT
            UID:test-event-1@ironbrain
            DTSTART:20260925T100000Z
            DTEND:20260925T110000Z
            SUMMARY:Smoke test event
            END:VEVENT
            BEGIN:VEVENT
            UID:test-event-2@ironbrain
            DTSTART:20260926T100000Z
            DTEND:20260926T110000Z
            SUMMARY:Other day
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
            var options = Options.Create(new CalendarOptions
            {
                SourceUrl = prefix + "calendar.ics",
                PreferIcsFeed = true
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var sut = new CalDavCalendarService(factory, options, sp, NullLogger<CalDavCalendarService>.Instance);

            var day = await sut.GetAppointmentsForDayAsync("2026-09-25");
            Assert.Single(day);
            Assert.Equal("Smoke test event", day[0].Summary);
            Assert.Equal("test-event-1@ironbrain", day[0].Uid);
            Assert.Equal(11, day[0].End.Hour);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task GetAppointments_MultiCalendar_MergesAndSetsCalendarName()
    {
        var propfindXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/Calendar/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/Calendar/personal/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Personal</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/Calendar/Family/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Family</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/Calendar/personal.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Mirror</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        static string Report(string summary) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/event.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <c:calendar-data>BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:{summary}@ironbrain
            DTSTART:20260925T100000Z
            DTEND:20260925T110000Z
            SUMMARY:{summary}
            END:VEVENT
            END:VCALENDAR</c:calendar-data>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

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
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (HttpListenerException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
                string body;
                if (ctx.Request.HttpMethod == "PROPFIND")
                    body = propfindXml;
                else if (path.EndsWith("/personal", StringComparison.OrdinalIgnoreCase))
                    body = Report("From Personal");
                else if (path.EndsWith("/Family", StringComparison.OrdinalIgnoreCase))
                    body = Report("From Family");
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/xml";
                ctx.Response.StatusCode = 207;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        }, cts.Token);

        try
        {
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "Calendar/",
                SourceUrl = prefix + "Calendar/personal/",
                Username = "user@example.com",
                Password = "x"
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var sut = new CalDavCalendarService(factory, options, sp, NullLogger<CalDavCalendarService>.Instance);

            var items = await sut.GetAppointmentsForDayAsync("2026-09-25", new CalendarQueryOptions());
            Assert.Equal(2, items.Count);
            Assert.Contains(items, a => a.Summary == "From Personal" && a.CalendarName == "Personal" && a.CalendarId == "personal");
            Assert.Contains(items, a => a.Summary == "From Family" && a.CalendarName == "Family" && a.CalendarId == "Family");
            Assert.True(items[0].Start <= items[1].Start);

            var filtered = await sut.GetAppointmentsForDayAsync(
                "2026-09-25",
                new CalendarQueryOptions { CalendarFilters = ["Family"] });
            Assert.Single(filtered);
            Assert.Equal("Family", filtered[0].CalendarName);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* listener stopped */ }
        }
    }

    [Fact]
    public async Task AddAppointment_WithoutCalendar_WhenMultiple_Fails()
    {
        var propfindXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/Calendar/personal/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Personal</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/Calendar/Family/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:displayname>Family</d:displayname>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(propfindXml);
            ctx.Response.ContentType = "application/xml";
            ctx.Response.StatusCode = 207;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "Calendar/",
                Username = "u",
                Password = "p"
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var sut = new CalDavCalendarService(factory, options, sp, NullLogger<CalDavCalendarService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.AddAppointmentAsync(new CalendarAddRequest
                {
                    Summary = "Nope",
                    Start = new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc),
                    End = new DateTime(2026, 9, 26, 11, 0, 0, DateTimeKind.Utc)
                }));
            Assert.Contains("Specify --calendar/-c", ex.Message);
        }
        finally
        {
            listener.Stop();
            try { await serve; } catch { /* listener stopped */ }
        }
    }

    [Fact]
    public async Task ListCalendars_PropfindUsesTrailingSlash_AndReturnsSogoShared()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sogo-propfind-with-shared.xml"));

        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        string? requestedPath = null;
        using var cts = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                requestedPath = ctx.Request.Url?.AbsolutePath;
                var bytes = Encoding.UTF8.GetBytes(fixture);
                ctx.Response.StatusCode = 207;
                ctx.Response.ContentType = "application/xml";
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        }, cts.Token);

        try
        {
            // HomeUrl without trailing slash — service must still PROPFIND …/Calendar/
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "SOGo/dav/user@example.com/Calendar",
                Username = "user@example.com",
                Password = "x",
                IncludeSharedByDefault = true
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var sut = new CalDavCalendarService(
                sp.GetRequiredService<IHttpClientFactory>(),
                options,
                sp,
                NullLogger<CalDavCalendarService>.Instance);

            var all = await sut.ListCalendarsAsync(includeShared: true);
            Assert.Equal(7, all.Count);
            Assert.Contains(all, c => c.IsShared && c.CollectionId.Contains("alice_D_smith", StringComparison.Ordinal));
            Assert.True(
                requestedPath == "/SOGo/dav/user@example.com/Calendar/"
                || requestedPath == "/SOGo/dav/user@example.com/Calendar",
                $"Expected Calendar home path with trailing slash preference; got '{requestedPath}'");
            Assert.EndsWith("/", requestedPath); // SOGo delegated calendars need the slash

            var ownedOnly = await sut.ListCalendarsAsync(includeShared: false);
            Assert.Equal(6, ownedOnly.Count);
            Assert.DoesNotContain(ownedOnly, c => c.IsShared);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task ListCalendars_SendsNonEmptyUserAgent_OnPropfind()
    {
        // Documents SOGo quirk: empty/missing User-Agent → delegated calendars omitted from Depth:1 PROPFIND.
        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        string? userAgent = null;
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            userAgent = ctx.Request.Headers["User-Agent"];
            var body = """
                <?xml version="1.0" encoding="utf-8"?>
                <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
                  <d:response>
                    <d:href>/Calendar/personal/</d:href>
                    <d:propstat>
                      <d:status>HTTP/1.1 200 OK</d:status>
                      <d:prop>
                        <d:displayname>Personal</d:displayname>
                        <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                      </d:prop>
                    </d:propstat>
                  </d:response>
                </d:multistatus>
                """;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 207;
            ctx.Response.ContentType = "application/xml";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            var options = Options.Create(new CalendarOptions
            {
                HomeUrl = prefix + "Calendar/",
                Username = "u@example.com",
                Password = "x"
            });
            var services = new ServiceCollection();
            services.AddHttpClient(nameof(CalDavCalendarService));
            var sp = services.BuildServiceProvider();
            var sut = new CalDavCalendarService(
                sp.GetRequiredService<IHttpClientFactory>(),
                options,
                sp,
                NullLogger<CalDavCalendarService>.Instance);

            _ = await sut.ListCalendarsAsync();
            Assert.False(string.IsNullOrWhiteSpace(userAgent));
            Assert.Equal(CalDavCalendarService.CalDavUserAgent, userAgent);
        }
        finally
        {
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
