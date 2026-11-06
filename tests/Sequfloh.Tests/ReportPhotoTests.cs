using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Sequfloh.Tests;

/// <summary>Evidence photos are drawn in the chargeback PDF and the job report PDF. Made-up pictures only.</summary>
[Collection("api")]
public sealed class ReportPhotoTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Rights = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks];

    // A progressive JPEG (SOF2), 96 by 54, made up for this test.
    const string ProgressiveJpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wgARCAA2AGADASIAAhEBAxEB/8QAFwABAQEBAAAAAAAAAAAAAAAAAAUGB//EABkBAQEAAwEAAAAAAAAAAAAAAAAHBAUIBv/aAAwDAQACEAMQAAAB0gmHrk2lN2OLiBc5wAAB0kc91FNpTdji4gXOcAAAdJHPdRTaU3Y4uIFznAAAHSRz3UU02OLiBc5wAAB//8QAFxAAAwEAAAAAAAAAAAAAAAAABTA0EP/aAAgBAQABBQLScKycKycKycKycKycKycKP//EABsRAAAHAQAAAAAAAAAAAAAAAAEDBCA0cbEA/9oACAEDAQE/AeXRjKHHLoxlDjl0Yyhxy6MZQ43/xAAcEQAABwEBAAAAAAAAAAAAAAABAgMFIDRysQD/2gAIAQIBAT8B80X0Nl6Emi+hsvQk0X0Nl6Emi+hsvQj/AP/EABgQAAMBAQAAAAAAAAAAAAAAAAMwgbEQ/9oACAEBAAY/AukmsJNYSawk1hJrCTWEmp//xAAXEAADAQAAAAAAAAAAAAAAAAAwYfAQ/9oACAEBAAE/IdoQlCEoQlCEoQlCEoQP/9oADAMBAAIAAwAAABAMAIIIIMAIIIIMAIIIIMAIIIL/xAAXEQADAQAAAAAAAAAAAAAAAAAgUfAA/9oACAEDAQE/ENPYp7FPYp7D/8QAFBEBAAAAAAAAAAAAAAAAAAAAQP/aAAgBAgEBPxBCJEiRf//EABQQAQAAAAAAAAAAAAAAAAAAAGD/2gAIAQEAAT8QKDjjjjjj/wD/2Q==";

    async Task<(Guid Id, ApiClient Client)> JobAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, client) = await app.CreateUserAsync(Rights, site);
        Guid wi;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var w = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Made-up photo job", Number = "DWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site, Status = WorkInstructionStatus.Released };
            w.FamilyId = w.Id;
            db.SequflohWorkInstructions.Add(w);
            await db.SaveChangesAsync();
            wi = w.Id;
        }
        var id = (await (await client.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        return (id, client);
    }

    static async Task Upload(ApiClient client, Guid id, string name, byte[] bytes)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", name }, { new StringContent("evidence"), "kind" } };
        var sent = await client.Http.PostAsync($"{U}/{id}/job/test-files", form);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
    }

    static byte[] Jpeg(int w, int h, SKColor fill, SKColor band)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(fill);
            using var paint = new SKPaint { Color = band };
            canvas.DrawRect(new SKRect(0, 0, w / 3f, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    static byte[] PngWithAlpha(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(220, 40, 40, 140) };
            canvas.DrawRect(new SKRect(0, 0, w, h), paint);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Inserts an EXIF orientation tag (6 = rotate 90° clockwise) after the JPEG start.</summary>
    static byte[] WithOrientation6(byte[] jpeg)
    {
        var payload = new byte[32];
        "Exif\0\0"u8.CopyTo(payload);
        payload[6] = (byte)'I'; payload[7] = (byte)'I'; payload[8] = 42; payload[10] = 8;
        payload[14] = 1; // one IFD entry
        payload[16] = 0x12; payload[17] = 0x01; // Orientation
        payload[18] = 3; // SHORT
        payload[20] = 1; // one value
        payload[24] = 6; // rotate 90° clockwise
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF; segment[1] = 0xE1;
        segment[2] = 0; segment[3] = (byte)(payload.Length + 2);
        payload.CopyTo(segment, 4);
        var result = new byte[jpeg.Length + segment.Length];
        result[0] = 0xFF; result[1] = 0xD8;
        segment.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    static List<(int W, int H)> Images(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return Regex.Matches(text, @"/Subtype /Image /Width (\d+) /Height (\d+)")
            .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToList();
    }

    static async Task Keep(string name, byte[] pdf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sequfloh-photo-pdfs");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name), pdf);
    }

    [Fact]
    public async Task Evidence_photos_are_drawn_in_the_job_report_and_the_chargeback_report()
    {
        var (id, client) = await JobAsync();
        Assert.False((await client.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("photos").GetBoolean());

        var progressive = Convert.FromBase64String(ProgressiveJpeg);
        Assert.True(progressive.AsSpan().IndexOf(new byte[] { 0xFF, 0xC2 }) >= 0);
        var png = PngWithAlpha(100, 140);
        Assert.Equal(0x89, png[0]);
        var rotated = WithOrientation6(Jpeg(240, 80, SKColors.DodgerBlue, SKColors.OrangeRed));
        await Upload(client, id, "wide-shot.jpg", Jpeg(2200, 1400, SKColors.SeaGreen, SKColors.OrangeRed));
        await Upload(client, id, "progressive.jpg", progressive);
        await Upload(client, id, "see-through.png", png);
        await Upload(client, id, "phone-rotated.jpg", rotated);

        Assert.True((await client.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("photos").GetBoolean());
        Assert.True((await client.GetElAsync($"{U}/{id}/job/report")).GetProperty("options").GetProperty("photos").GetBoolean());

        var jobPdf = await (await client.GetAsync($"{U}/{id}/job/report/pdf")).Content.ReadAsByteArrayAsync();
        await Keep("job-report.pdf", jobPdf);
        var jobText = Encoding.Latin1.GetString(jobPdf);
        var jobImages = Images(jobPdf);
        Assert.Equal(4, jobImages.Count);
        Assert.Contains((1600, 1018), jobImages); // 2200x1400 brought down to a 1600 long edge
        Assert.Contains((96, 54), jobImages);
        Assert.Contains((100, 140), jobImages);
        Assert.Contains((80, 240), jobImages); // 240x80 stored on its side, shown upright
        Assert.DoesNotContain("attached separately", jobText);
        Assert.DoesNotContain("could not be shown", jobText);
        foreach (var name in new[] { "wide-shot.jpg", "progressive.jpg", "see-through.png", "phone-rotated.jpg" })
            Assert.Contains(name, jobText);
        Assert.DoesNotContain(Encoding.Latin1.GetString(progressive), jobText);
        Assert.DoesNotContain(Encoding.Latin1.GetString(png), jobText);

        var created = await (await client.PostOkAsync(C, new { workOrderNumber = "WO-PHOTO-1", trackingRecordId = id, supplier = "Made-up Supplier Co", partNumber = "E-100", taskDescription = "Made-up inspection of a cracked housing" })).JsonAsync();
        Assert.Contains("photos", created.GetProperty("sections").EnumerateArray().Select(e => e.GetString()));
        var cbPdf = await (await client.GetAsync($"{C}/{created.Id()}/pdf")).Content.ReadAsByteArrayAsync();
        await Keep("chargeback-report.pdf", cbPdf);
        var cbText = Encoding.Latin1.GetString(cbPdf);
        Assert.Equal(jobImages, Images(cbPdf));
        Assert.DoesNotContain("attached separately", cbText);
        Assert.Contains("Made-up Supplier Co", cbText);
        Assert.Contains("see-through.png", cbText);

        var view = await client.GetElAsync($"{C}/{created.Id()}/view");
        var captions = view.EnumerateArray().SelectMany(b => b.TryGetProperty("photos", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Array ? p.EnumerateArray().Select(x => x.GetString() ?? "") : []).ToList();
        Assert.Contains(captions, c => c.Contains("phone-rotated.jpg"));
        using (var wb = new XLWorkbook(new MemoryStream(await (await client.GetAsync($"{C}/{created.Id()}/xlsx")).Content.ReadAsByteArrayAsync())))
        {
            var sheet = string.Join("|", wb.Worksheet(1).CellsUsed().Select(cell => cell.GetFormattedString()));
            Assert.Contains("Photo", sheet);
            Assert.Contains("wide-shot.jpg", sheet);
        }

        // an untick is remembered, and a saved section list that leaves photos off stays off
        await client.PutOkAsync($"{U}/{id}/job/report-options", new { photos = false });
        Assert.False((await client.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("photos").GetBoolean());
        var off = Encoding.Latin1.GetString(await (await client.GetAsync($"{U}/{id}/job/report/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Empty(Images(Encoding.Latin1.GetBytes(off)));
        Assert.DoesNotContain("Photos and evidence", off);

        var defaults = (await client.GetElAsync($"{C}/sections")).EnumerateArray().Where(o => o.GetProperty("default").GetBoolean()).Select(o => o.GetProperty("key").GetString()).ToArray();
        Assert.DoesNotContain("photos", defaults);
        var kept = await (await client.PostOkAsync(C, new { workOrderNumber = "WO-PHOTO-2", trackingRecordId = id, supplier = "Made-up Supplier Co", sections = defaults })).JsonAsync();
        Assert.DoesNotContain("photos", kept.GetProperty("sections").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(Images(await (await client.GetAsync($"{C}/{kept.Id()}/pdf")).Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task A_photo_that_cannot_be_read_is_named_and_the_others_are_still_shown()
    {
        var (id, client) = await JobAsync();
        await Upload(client, id, "good.jpg", Jpeg(80, 60, SKColors.Goldenrod, SKColors.Brown));
        await Upload(client, id, "broken.jpg", "this is not a picture"u8.ToArray());

        var job = await (await client.GetAsync($"{U}/{id}/job/report/pdf")).Content.ReadAsByteArrayAsync();
        var jobText = Encoding.Latin1.GetString(job);
        Assert.Single(Images(job));
        Assert.Contains("could not be shown", jobText);
        Assert.Contains("broken.jpg", jobText);
        Assert.Contains("good.jpg", jobText);
        Assert.DoesNotContain("attached separately", jobText);

        var created = await (await client.PostOkAsync(C, new { workOrderNumber = "WO-PHOTO-BAD", trackingRecordId = id, supplier = "Made-up Supplier Co" })).JsonAsync();
        var cbText = Encoding.Latin1.GetString(await (await client.GetAsync($"{C}/{created.Id()}/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Single(Images(Encoding.Latin1.GetBytes(cbText)));
        Assert.Contains("could not be shown", cbText);
        Assert.Contains("broken.jpg", cbText);
        Assert.DoesNotContain("attached separately", cbText);
    }
}
