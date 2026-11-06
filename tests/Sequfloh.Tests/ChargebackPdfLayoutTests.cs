using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;
using SkiaSharp;

namespace Sequfloh.Tests;

/// <summary>Chargeback PDFs keep each photo with its heading and still draw the period chart. Made-up pictures only.</summary>
[Collection("api")]
public sealed class ChargebackPdfLayoutTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Mgr = [Permissions.SequflohAccess, Permissions.SequflohManageProjects, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime,
        Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks];

    [Fact]
    public async Task Project_and_single_work_order_pdfs_keep_photos_with_their_heading_and_draw_the_chart()
    {
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (uid, mgr) = await app.CreateUserAsync(Mgr, site);
        var pid = (await (await mgr.PostOkAsync("/api/sequfloh/projects", new { number = $"LAY-{tag}", name = "Made-up layout project", siteId = site })).JsonAsync()).Id();
        async Task<Guid> Wo(string n) => (await (await mgr.PostOkAsync(T, new { type = "Inspection", siteId = site, number = n, projectId = pid })).JsonAsync()).Id();
        var aNo = $"LA-{tag}"; var bNo = $"LB-{tag}";
        var a = await Wo(aNo); var b = await Wo(bNo);
        var start = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == a).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, "34BW5083"));
            await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == b).ExecuteUpdateAsync(u => u.SetProperty(r => r.PartNumber, "34BW5084"));
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = a, TrackingNumber = aNo, UserId = uid, UserName = "Made-up Tech", StartedAt = start, StoppedAt = start.AddHours(2), Seconds = 2 * 3600 });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = b, TrackingNumber = bNo, UserId = uid, UserName = "Made-up Tech", StartedAt = start.AddHours(3), StoppedAt = start.AddHours(6), Seconds = 3 * 3600 });
            await db.SaveChangesAsync();
        }
        // Tall pictures: at full page width they are taller than the page, which is what left a blank page between the heading and the first photo.
        await Upload(mgr, a, "crack.jpg", Jpeg(900, 1600));
        await Upload(mgr, a, "edge.jpg", Jpeg(800, 500));
        await Upload(mgr, b, "seal.png", Png(700, 1200));

        var sections = new[] { "header", "job", "labor", "linkedWorkOrders", "photos", "cost", "sumCharts", "signoff" };
        var project = await (await mgr.PostOkAsync(C, new
        {
            workOrderNumber = "", hourlyRate = 40m, workOrderIds = new[] { a, b }, sections,
            period = new { kind = "Project", projectId = pid },
        })).JsonAsync();
        var projectPdf = await (await mgr.GetAsync($"{C}/{project.Id()}/pdf")).Content.ReadAsByteArrayAsync();
        AssertLayout(projectPdf, "project");

        var one = await (await mgr.PostOkAsync(C, new
        {
            workOrderNumber = $"WO-{tag}", trackingRecordId = a, hourlyRate = 25m, sections,
            period = new { kind = "Day", from = "2026-10-05" },
        })).JsonAsync();
        var onePdf = await (await mgr.GetAsync($"{C}/{one.Id()}/pdf")).Content.ReadAsByteArrayAsync();
        AssertLayout(onePdf, "single work order");
    }

    static void AssertLayout(byte[] pdf, string kind)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var pages = PageStreams(text);
        Assert.True(pages.Count >= 2, $"{kind} pdf has {pages.Count} page(s)");
        for (var i = 0; i < pages.Count; i++)
        {
            var body = BodyText(pages[i]);
            var image = pages[i].Contains(" Do");
            Assert.True(image || body.Count > 0, $"{kind} page {i + 1} is blank");
            if (body.Any(t => t.Contains("Photos and evidence")))
                Assert.True(image, $"{kind} page {i + 1} has the photos heading and no picture");
        }
        Assert.Contains("Where the time went", text);
        Assert.Matches(@"0\.071 0\.341 0\.769 rg [\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ re f", text);
        Assert.True(Regex.Matches(text, @"/Subtype /Image").Count >= 2, $"{kind} pdf is missing photos");
    }

    static List<string> PageStreams(string pdf)
    {
        var pages = new List<string>();
        var i = 0;
        while (true)
        {
            var s = pdf.IndexOf("stream\n", i, StringComparison.Ordinal);
            if (s < 0) break;
            var e = pdf.IndexOf("\nendstream", s, StringComparison.Ordinal);
            if (e < 0) break;
            var body = pdf[(s + 7)..e];
            if (body.Contains("Tj")) pages.Add(body);
            i = e + 9;
        }
        return pages;
    }

    static List<string> BodyText(string stream)
    {
        var texts = Regex.Matches(stream, @"\((?:\\.|[^)\\])*\) Tj").Select(m => m.Value[1..^4].Replace("\\(", "(").Replace("\\)", ")")).ToList();
        return texts.Where(t => !IsRunningHeader(t)).ToList();
    }

    static bool IsRunningHeader(string t) =>
        t.Contains("SEQUFLOH") || t.StartsWith("Printed ", StringComparison.Ordinal) || t.StartsWith("Page ", StringComparison.Ordinal)
        || t.StartsWith("Chargeback report", StringComparison.Ordinal) || t.StartsWith("Chargeback Report", StringComparison.Ordinal)
        || t is "Test Organization";

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
            canvas.DrawRect(new SKRect(0, 0, w / 5f, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(20, 80, 180, 220));
            using var paint = new SKPaint { Color = new SKColor(240, 200, 40, 230) };
            canvas.DrawRect(new SKRect(w * 0.7f, 0, w, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
