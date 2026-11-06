using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using SkiaSharp;

namespace Sequfloh.Tests;

/// <summary>A chargeback report on a project links its work orders and totals their time. Made-up data only.</summary>
[Collection("api")]
public sealed class ProjectChargebackTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Mgr = [Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime,
        Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks, Permissions.SequflohDeleteChargebacks, Permissions.SequflohRemoveTime];

    sealed record Setup(Guid Site, ApiClient Mgr, Guid Project, string Tag, Guid A, Guid B, Guid X, string ANo, string BNo, string XNo, DateTimeOffset Start);

    async Task<Setup> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (uid, mgr) = await app.CreateUserAsync(Mgr, site);
        var pid = (await (await mgr.PostOkAsync("/api/sequfloh/projects", new { number = $"PCB-{tag}", name = "Made-up project", siteId = site })).JsonAsync()).Id();
        async Task<Guid> Wo(string n) => (await (await mgr.PostOkAsync(T, new { type = "Inspection", siteId = site, number = n, projectId = pid })).JsonAsync()).Id();
        var a = await Wo($"PA-{tag}"); var b = await Wo($"PB-{tag}"); var x = await Wo($"PX-{tag}");
        var start = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        // A: a crew of 2 for 3 h with a 1 h pause (4 labor h) and a registered person 2 h; B: a day worker 3 h with a half hour pause; X: 5 h of crew, left out of the report
        var worker = (await (await mgr.PostOkAsync($"{T}/{b}/crew/workers", new { name = "Made-up Dee" })).JsonAsync()).GetProperty("workers").EnumerateArray().First().Id();
        await mgr.PostOkAsync($"{T}/{b}/crew/sessions", new { crewSize = 1, workerId = worker, start = start.AddDays(2), stop = start.AddDays(2).AddHours(3), pauses = new[] { new { from = start.AddDays(2).AddHours(1), to = start.AddDays(2).AddHours(1.5) } } });
        using (var s = app.Services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a || r.Id == x).ExecuteUpdateAsync(u => u.SetProperty(r => r.UseCrewTime, true));
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a || r.Id == b || r.Id == x).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, $"MU-{tag}"));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = a, TrackingNumber = $"PA-{tag}", CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(3), PausedSeconds = 3600, Entered = true });
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = x, TrackingNumber = $"PX-{tag}", CrewSize = 1, StartedAt = start, StoppedAt = start.AddHours(5), Entered = true });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = a, TrackingNumber = $"PA-{tag}", UserId = uid, UserName = "Made-up Tech", StartedAt = start, StoppedAt = start.AddHours(2), Seconds = 7200 });
            await db.SaveChangesAsync();
        }
        return new(site, mgr, pid, tag, a, b, x, $"PA-{tag}", $"PB-{tag}", $"PX-{tag}", start);
    }

    [Fact]
    public async Task A_project_report_totals_the_ticked_work_orders_and_shows_each_one()
    {
        var s = await SetupAsync();
        var pre = await (await s.Mgr.PostOkAsync($"{C}/prefill-linked", new { workOrderIds = new[] { s.A, s.B } })).JsonAsync();
        Assert.Equal(8.5m, pre.GetProperty("totalHours").GetDecimal()); // 4 crew + 2 registered + 2.5 day worker
        Assert.Equal(4, pre.GetProperty("personnel").GetInt32()); // crew of 2, one person, one day worker
        Assert.Equal($"MU-{s.Tag}", pre.Str("partNumber"));

        var cb = await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "", hourlyRate = 40m, workOrderIds = new[] { s.A, s.B }, period = new { kind = "Project", projectId = s.Project },
            costLines = new[] { new { description = "Made-up freight", amount = 10m } } })).JsonAsync();
        var id = cb.Id();
        Assert.Equal($"PCB-{s.Tag}", cb.Str("workOrderNumber"));
        Assert.Equal(8.5m, cb.GetProperty("totalHours").GetDecimal());
        Assert.Equal(6.5m, cb.GetProperty("crewHours").GetDecimal());
        Assert.Equal(2m, cb.GetProperty("userHours").GetDecimal());
        Assert.Equal(4, cb.GetProperty("totalPersonnel").GetInt32());
        Assert.Equal("2026-10-05", cb.Str("dateFrom")); Assert.Equal("2026-10-07", cb.Str("dateTo"));
        Assert.Equal(340m, cb.GetProperty("laborCost").GetDecimal());
        Assert.Equal(350m, cb.GetProperty("total").GetDecimal());
        Assert.Equal(2, cb.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.Null(cb.Str("trackingRecordId"));

        var view = (await s.Mgr.GetElAsync($"{C}/{id}/view")).EnumerateArray().ToList();
        var table = view.First(b => b.Str("key") == "linkedWorkOrders");
        var rows = table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToArray()).ToList();
        Assert.Equal([s.ANo, s.BNo, "Total"], rows.Select(r => r[0]));
        Assert.Equal("6.00", rows[0][^1]); Assert.Equal("2.50", rows[1][^1]); Assert.Equal("8.50", rows[2][^1]);
        Assert.DoesNotContain(rows, r => r[0] == s.XNo);
        var job = view.First(b => b.Str("key") == "job").GetProperty("fields").EnumerateArray().ToDictionary(f => f.Str("label")!, f => f.Str("value"));
        Assert.Equal($"2: {s.ANo}, {s.BNo}", job["Linked work orders"]);
        Assert.StartsWith("8.50", job["Total man hours"]);

        var pdf = await s.Mgr.Http.GetAsync($"{C}/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var xl = await s.Mgr.Http.GetAsync($"{C}/{id}/xlsx");
        using (var wb = new XLWorkbook(new MemoryStream(await xl.Content.ReadAsByteArrayAsync())))
        {
            var cells = wb.Worksheet(1).CellsUsed().Select(c => c.GetString()).ToList();
            Assert.Contains(s.ANo, cells); Assert.Contains(s.BNo, cells); Assert.Contains("8.50", cells); Assert.Contains("340.00", cells);
        }

        using var sc = app.Services.CreateScope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id && a.Action == "ChargebackReportCreated").SingleAsync();
        Assert.Equal($"Chargeback report PCB-{s.Tag} created, linking 2 work orders ({s.ANo}, {s.BNo}), 8.50 man hours, 4 personnel", audit.Description);
    }

    [Fact]
    public async Task Saving_again_totals_again_and_changed_time_flags_the_report()
    {
        var s = await SetupAsync();
        var id = (await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "", workOrderIds = new[] { s.A, s.B }, period = new { kind = "Project", projectId = s.Project } })).JsonAsync()).Id();

        // time removed on B flags the report; saving it again with X ticked as well totals again and clears the flag, whatever hours are typed
        var row = (await s.Mgr.GetElAsync($"{T}/{s.B}/time-corrections")).GetProperty("entries").EnumerateArray().First().Id();
        await s.Mgr.PostOkAsync($"{T}/{s.B}/time-corrections/remove", new { crewSessionIds = new[] { row }, reason = "Made-up duplicate" });
        var flagged = await s.Mgr.GetElAsync($"{C}/{id}");
        Assert.Equal(JsonValueKind.String, flagged.GetProperty("timeChangedAt").ValueKind);

        var again = await (await s.Mgr.PutAsync($"{C}/{id}", new { workOrderNumber = $"PCB-{s.Tag}", totalHours = 99m, workOrderIds = new[] { s.A, s.B, s.X }, period = new { kind = "Project", projectId = s.Project } })).JsonAsync();
        Assert.Equal(11m, again.GetProperty("totalHours").GetDecimal()); // 6 on A, 0 left on B, 5 on X
        Assert.Equal(3, again.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("timeChangedAt").ValueKind);
    }

    [Fact]
    public async Task A_single_work_order_report_works_as_before_and_permissions_are_checked_per_work_order()
    {
        var s = await SetupAsync();
        var one = await (await s.Mgr.PostOkAsync(C, new { workOrderNumber = "WO-MADEUP-1", trackingRecordId = s.A })).JsonAsync();
        Assert.Equal(6m, one.GetProperty("totalHours").GetDecimal());
        Assert.Equal(s.A, one.GetProperty("trackingRecordId").GetGuid());
        Assert.Equal(0, one.GetProperty("linkedWorkOrders").GetArrayLength());
        Assert.DoesNotContain((await s.Mgr.GetElAsync($"{C}/{one.Id()}/view")).EnumerateArray(), b => b.Str("key") == "linkedWorkOrders");

        // the chargeback permission for another site only: refused for these work orders
        var other = await app.CreateSiteAsync();
        var (_, elsewhere) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks], other);
        Assert.Equal(HttpStatusCode.Forbidden, (await elsewhere.PostAsync(C, new { workOrderNumber = "X", workOrderIds = new[] { s.A } })).StatusCode);
    }

    /// <summary>
    /// Two linked work orders, each with a registered person's clocked time and one evidence photo.
    /// Use crew time stays at its default of No, so a nameless crew entry on the first work order must not be counted.
    /// </summary>
    [Fact]
    public async Task Linked_work_orders_show_their_photos_and_the_project_chargeback_is_totalled()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (uid, mgr) = await app.CreateUserAsync(Mgr, site);
        var pid = (await (await mgr.PostOkAsync("/api/sequfloh/projects", new { number = $"PCB-{tag}", name = "Made-up linked project", siteId = site })).JsonAsync()).Id();
        async Task<Guid> Wo(string n) => (await (await mgr.PostOkAsync(T, new { type = "Inspection", siteId = site, number = n, projectId = pid })).JsonAsync()).Id();
        var aNo = $"PA-{tag}"; var bNo = $"PB-{tag}";
        var a = await Wo(aNo); var b = await Wo(bNo);
        var start = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Use crew time is left at No. The nameless crew entry is kept and must not add hours.
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, "34BW5083"));
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == b).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, "34BW5084"));
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = a, TrackingNumber = aNo, CrewSize = 4, StartedAt = start, StoppedAt = start.AddHours(10), Entered = true });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = a, TrackingNumber = aNo, UserId = uid, UserName = "Made-up Tech", StartedAt = start, StoppedAt = start.AddHours(2), Seconds = 2 * 3600 });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = b, TrackingNumber = bNo, UserId = uid, UserName = "Made-up Tech", StartedAt = start.AddDays(1), StoppedAt = start.AddDays(1).AddHours(3.5), Seconds = (long)(3.5 * 3600) });
            await db.SaveChangesAsync();
        }
        await Upload(mgr, a, "crack.jpg", Jpeg(320, 48));
        await Upload(mgr, b, "seal.png", Png(280, 36));

        var pre = await (await mgr.PostOkAsync($"{C}/prefill-linked", new { workOrderIds = new[] { a, b } })).JsonAsync();
        Assert.True(pre.GetProperty("hasPhotos").GetBoolean());
        Assert.Equal(5.5m, pre.GetProperty("totalHours").GetDecimal()); // 2 h + 3.5 h of registered time; the crew of 4 for 10 h is not counted

        // no section list saved: photos come on because a linked work order has evidence
        var hinted = await (await mgr.PostOkAsync(C, new { workOrderNumber = "", workOrderIds = new[] { a, b }, period = new { kind = "Project", projectId = pid } })).JsonAsync();
        Assert.Contains("photos", hinted.GetProperty("sections").EnumerateArray().Select(e => e.GetString()));
        Assert.True(hinted.GetProperty("hasPhotos").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.DeleteAsync($"{C}/{hinted.Id()}")).StatusCode);

        // a single-work-order chargeback in the same project still counts, beside the linked one
        await mgr.PostOkAsync(C, new { workOrderNumber = $"WO-{tag}", trackingRecordId = a, hourlyRate = 10m, qtyInspected = 2, qtyBad = 1 });

        var sections = new[] { "header", "job", "labor", "linkedWorkOrders", "photos", "cost", "sumChargebacks", "signoff" };
        var cb = await (await mgr.PostOkAsync(C, new
        {
            workOrderNumber = "", hourlyRate = 40m, qtyInspected = 10, qtyBad = 4, workOrderIds = new[] { a, b }, sections,
            period = new { kind = "Project", projectId = pid },
        })).JsonAsync();
        var id = cb.Id();
        Assert.Equal(5.5m, cb.GetProperty("totalHours").GetDecimal());
        Assert.Equal(220m, cb.GetProperty("laborCost").GetDecimal()); // 5.50 × 40
        Assert.Contains("photos", cb.GetProperty("sections").EnumerateArray().Select(e => e.GetString()));

        var view = (await mgr.GetElAsync($"{C}/{id}/view")).EnumerateArray().ToList();
        string Field(string key, string label) => view.Single(b => b.Str("key") == key && b.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array && f.EnumerateArray().Any(x => x.Str("label") == label))
            .GetProperty("fields").EnumerateArray().Single(x => x.Str("label") == label).Str("value");
        Assert.StartsWith("5.50", Field("job", "Total man hours"));
        var rows = view.Single(b => b.Str("key") == "linkedWorkOrders").GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToArray()).ToList();
        Assert.Equal([aNo, bNo, "Total"], rows.Select(r => r[0]));
        Assert.Equal("2.00", rows[0][^1]); Assert.Equal("3.50", rows[1][^1]); Assert.Equal("5.50", rows[2][^1]);
        Assert.Equal("220.00", Field("cost", "Labor"));
        Assert.StartsWith("220.00", Field("cost", "Total chargeback"));
        // the linked project chargeback itself, plus the single-work-order chargeback, not a zero total
        Assert.Equal("2", Field("sumChargebacks", "Chargeback reports"));
        Assert.Equal("5", Field("sumChargebacks", "Qty bad charged back"));
        Assert.Equal("7.50", Field("sumChargebacks", "Hours charged back")); // 2.00 on the single work order + 5.50 on this report
        Assert.Equal("240.00", Field("sumChargebacks", "Total chargeback")); // 20.00 + 220.00

        var captions = view.SelectMany(b => b.TryGetProperty("photos", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "") : []).ToList();
        Assert.Contains(captions, c => c.Contains(aNo) && c.Contains("crack.jpg"));
        Assert.Contains(captions, c => c.Contains(bNo) && c.Contains("seal.png"));

        var pdfBytes = await (await mgr.GetAsync($"{C}/{id}/pdf")).Content.ReadAsByteArrayAsync();
        var pdf = Encoding.Latin1.GetString(pdfBytes);
        var images = Regex.Matches(pdf, @"/Subtype /Image /Width (\d+) /Height (\d+)").Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToList();
        Assert.Equal(2, images.Count);
        Assert.Contains((320, 48), images);
        Assert.Contains((280, 36), images);
        Assert.Contains(aNo, pdf); Assert.Contains("crack.jpg", pdf);
        Assert.Contains(bNo, pdf); Assert.Contains("seal.png", pdf);
        Assert.Contains("7.50", pdf); Assert.Contains("240.00", pdf);

        using (var wb = new XLWorkbook(new MemoryStream(await (await mgr.GetAsync($"{C}/{id}/xlsx")).Content.ReadAsByteArrayAsync())))
        {
            var cells = string.Join("|", wb.Worksheet(1).CellsUsed().Select(c => c.GetString()));
            Assert.Contains("Photo", cells); Assert.Contains("crack.jpg", cells); Assert.Contains("seal.png", cells);
            Assert.Contains(aNo, cells); Assert.Contains(bNo, cells); Assert.Contains("7.50", cells); Assert.Contains("240.00", cells);
        }

        var project = await mgr.GetElAsync($"/api/sequfloh/projects/{pid}");
        Assert.Equal(2, project.GetProperty("totals").GetProperty("chargebackReports").GetInt32());
        Assert.Equal(240m, project.GetProperty("totals").GetProperty("chargebackTotal").GetDecimal());
    }

    static async Task Upload(ApiClient client, Guid id, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", name }, { new StringContent("evidence"), "kind" } };
        var sent = await client.Http.PostAsync($"{T}/{id}/job/test-files", form);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
    }

    static byte[] Jpeg(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.SeaGreen);
            using var paint = new SKPaint { Color = SKColors.OrangeRed };
            canvas.DrawRect(new SKRect(0, 0, w / 4f, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(20, 80, 180, 200) };
            canvas.DrawRect(new SKRect(0, 0, w, h), paint);
            paint.Color = new SKColor(240, 200, 40, 230);
            canvas.DrawRect(new SKRect(w * 0.7f, 0, w, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
