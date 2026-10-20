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

    [Fact]
    public async Task List_PopulatesHrefETagAndRecurrenceId_ForRecurringSeries()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:recur-1@ironbrain
            DTSTART:20260928T100000Z
            DTEND:20260928T110000Z
            RRULE:FREQ=DAILY;COUNT=3
            SUMMARY:Daily standup
            END:VEVENT
            END:VCALENDAR
            """;
        var reportXml = BuildReportMultistatus("/Calendar/personal/recur-1.ics", "\"etag-r1\"", ics);

        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(reportXml);
            ctx.Response.StatusCode = 207;
            ctx.Response.ContentType = "application/xml";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });

        try
        {
            var sut = CreateSut(prefix + "Calendar/personal/");
            var items = await sut.GetAppointmentsForDayAsync(
                "2026-09-28",
                new CalendarQueryOptions { CalendarUrl = prefix + "Calendar/personal/" });

            Assert.NotEmpty(items);
            Assert.All(items, a =>
            {
                Assert.Equal("recur-1@ironbrain", a.Uid);
                Assert.True(a.IsRecurring);
                Assert.NotNull(a.RecurrenceId);
                Assert.Equal("\"etag-r1\"", a.ETag);
                Assert.Contains("/Calendar/personal/recur-1.ics", a.Href, StringComparison.Ordinal);
            });
            Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc), items[0].RecurrenceId);
        }
        finally
        {
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task UpdateAppointment_PartialLocation_PutsWithIfMatch_AndBumpsSequence()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:evt-1@ironbrain
            DTSTART:20260928T140000Z
            DTEND:20260928T150000Z
            SUMMARY:Sync
            LOCATION:Berlin
            SEQUENCE:1
            END:VEVENT
            END:VCALENDAR
            """;
        string? putBody = null;
        string? ifMatch = null;
        string? putPath = null;

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

                if (ctx.Request.HttpMethod == "REPORT")
                {
                    var report = BuildReportMultistatus("/Calendar/personal/evt-1.ics", "\"etag-v1\"", ics);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    putPath = ctx.Request.Url?.AbsolutePath;
                    ifMatch = ctx.Request.Headers["If-Match"];
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    putBody = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 204;
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
            var collection = prefix + "Calendar/personal/";
            var sut = CreateSut(sourceUrl: collection);
            var msg = await sut.UpdateAppointmentAsync(new CalendarUpdateRequest
            {
                Uid = "evt-1@ironbrain",
                CalendarUrl = collection,
                Location = "Hamburg"
            });

            Assert.Contains("updated", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("\"etag-v1\"", ifMatch);
            Assert.Contains("/Calendar/personal/evt-1.ics", putPath, StringComparison.Ordinal);
            Assert.NotNull(putBody);
            Assert.Contains("LOCATION:Hamburg", putBody, StringComparison.Ordinal);
            Assert.Contains("SUMMARY:Sync", putBody, StringComparison.Ordinal);
            Assert.Contains("SEQUENCE:2", putBody, StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task UpdateAppointment_ETagConflict_Throws()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:evt-conflict@ironbrain
            DTSTART:20260928T140000Z
            DTEND:20260928T150000Z
            SUMMARY:Sync
            SEQUENCE:0
            END:VEVENT
            END:VCALENDAR
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
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                if (ctx.Request.HttpMethod == "REPORT")
                {
                    var report = BuildReportMultistatus("/Calendar/personal/evt-conflict.ics", "\"etag-old\"", ics);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    ctx.Response.StatusCode = 412;
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
            var collection = prefix + "Calendar/personal/";
            var sut = CreateSut(sourceUrl: collection);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sut.UpdateAppointmentAsync(new CalendarUpdateRequest
                {
                    Uid = "evt-conflict@ironbrain",
                    CalendarUrl = collection,
                    Summary = "Nope"
                }));
            Assert.Contains("412", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ETag", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task UpdateAppointment_SingleOccurrence_WritesRecurrenceIdOverride()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:recur-upd@ironbrain
            DTSTART:20260928T100000Z
            DTEND:20260928T110000Z
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Daily
            SEQUENCE:0
            END:VEVENT
            END:VCALENDAR
            """;
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

                if (ctx.Request.HttpMethod == "REPORT")
                {
                    var report = BuildReportMultistatus("/Calendar/personal/recur-upd.ics", "\"e1\"", ics);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    putBody = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 204;
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
            var collection = prefix + "Calendar/personal/";
            var sut = CreateSut(sourceUrl: collection);
            await sut.UpdateAppointmentAsync(new CalendarUpdateRequest
            {
                Uid = "recur-upd@ironbrain",
                CalendarUrl = collection,
                RecurrenceId = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
                Summary = "Daily (moved)"
            });

            Assert.NotNull(putBody);
            Assert.Contains("RECURRENCE-ID", putBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SUMMARY:Daily (moved)", putBody, StringComparison.Ordinal);
            // Master still present
            Assert.Contains("RRULE:FREQ=DAILY", putBody, StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task DeleteAppointment_Series_SendsDeleteWithIfMatch()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:del-series@ironbrain
            DTSTART:20260928T140000Z
            DTEND:20260928T150000Z
            SUMMARY:Temp
            END:VEVENT
            END:VCALENDAR
            """;
        string? deletePath = null;
        string? ifMatch = null;

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

                if (ctx.Request.HttpMethod == "REPORT")
                {
                    var report = BuildReportMultistatus("/Calendar/personal/del-series.ics", "\"etag-del\"", ics);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "DELETE")
                {
                    deletePath = ctx.Request.Url?.AbsolutePath;
                    ifMatch = ctx.Request.Headers["If-Match"];
                    ctx.Response.StatusCode = 204;
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
            var collection = prefix + "Calendar/personal/";
            var sut = CreateSut(sourceUrl: collection);
            var msg = await sut.DeleteAppointmentAsync(new CalendarDeleteRequest
            {
                Uid = "del-series@ironbrain",
                CalendarUrl = collection
            });

            Assert.Contains("whole series", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("\"etag-del\"", ifMatch);
            Assert.Contains("/Calendar/personal/del-series.ics", deletePath, StringComparison.Ordinal);
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    [Fact]
    public async Task DeleteAppointment_SingleOccurrence_AddsExdate()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:recur-del@ironbrain
            DTSTART:20260928T100000Z
            DTEND:20260928T110000Z
            RRULE:FREQ=DAILY;COUNT=5
            SUMMARY:Daily
            SEQUENCE:0
            END:VEVENT
            END:VCALENDAR
            """;
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

                if (ctx.Request.HttpMethod == "REPORT")
                {
                    var report = BuildReportMultistatus("/Calendar/personal/recur-del.ics", "\"e1\"", ics);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    ctx.Response.StatusCode = 207;
                    ctx.Response.ContentType = "application/xml";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (ctx.Request.HttpMethod == "PUT")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                    putBody = await reader.ReadToEndAsync();
                    ctx.Response.StatusCode = 204;
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
            var collection = prefix + "Calendar/personal/";
            var sut = CreateSut(sourceUrl: collection);
            await sut.DeleteAppointmentAsync(new CalendarDeleteRequest
            {
                Uid = "recur-del@ironbrain",
                CalendarUrl = collection,
                RecurrenceId = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc)
            });

            Assert.NotNull(putBody);
            Assert.Contains("EXDATE", putBody, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("20260930T100000", putBody, StringComparison.Ordinal);
            Assert.Contains("SEQUENCE:1", putBody, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP DELETE", putBody, StringComparison.Ordinal); // still a PUT of master
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await serve; } catch { /* stopped */ }
        }
    }

    private static CalDavCalendarService CreateSut(string? sourceUrl = null, string? homeUrl = null, string? defaultWrite = null)
    {
        var options = Options.Create(new CalendarOptions
        {
            SourceUrl = sourceUrl,
            HomeUrl = homeUrl,
            Username = "u",
            Password = "p",
            DefaultWriteCalendar = defaultWrite
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

    private static string BuildReportMultistatus(string href, string etag, string ics)
    {
        var escaped = System.Security.SecurityElement.Escape(ics) ?? ics;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>{href}</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:getetag>{etag}</d:getetag>
                    <c:calendar-data>{escaped}</c:calendar-data>
                  </d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;
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
