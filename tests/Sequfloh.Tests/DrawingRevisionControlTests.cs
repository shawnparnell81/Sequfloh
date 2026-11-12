using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Drawings;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Drawing revision comes from the Part Master, is snapshotted on a work order, and a released file is not replaced.</summary>
[Collection("api")]
public sealed class DrawingRevisionControlTests(TestApp app)
{
    const string D = "/api/drawings";
    const string I = "/api/drawings/import";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];
    static readonly string[] Drawings = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings, Permissions.SequflohReviseDrawings, Permissions.SequflohCreateProductionTask];

    static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% made-up test drawing {text}\n%%EOF\n");

    [Fact]
    public void A_drawing_number_is_kept_whole_and_a_trailing_letter_is_not_a_revision()
    {
        // A made-up brake-wear-sensor style number: the final letter is part of the number, not a revision suffix.
        Assert.Equal("BWS4401A", DrawingRevisionRules.WholeNumber("  BWS4401A  "));
        Assert.Equal("BWS4401A", DrawingPartMatcher.Normalize("BWS4401A.pdf"));
        Assert.NotEqual(DrawingPartMatcher.Normalize("BWS4401"), DrawingPartMatcher.Normalize("BWS4401A.pdf"));
        Assert.Equal("C", DrawingRevisionRules.Resolve("A", ["C"], "BWS4401A", partMasterWins: true).Revision);
        Assert.True(DrawingRevisionRules.Resolve(null, ["C"], "BWS4401A", partMasterWins: false).FromPartMaster);
        Assert.Contains("more than one revision", Assert.Throws<AppException>(() => DrawingRevisionRules.Resolve("A", ["B", "C"], "BWS4401A", false)).Message);
        Assert.Contains("already recorded", DrawingRevisionRules.Conflict("C", "D"));
        Assert.Null(DrawingRevisionRules.Conflict("C", "c"));
        Assert.Null(DrawingRevisionRules.Conflict(null, "C"));
        Assert.Equal(1, SequflohPartsImport.Suggest(["DWG #", "Drawing revision"])["drawingRevision"]);
        Assert.Equal(1, SequflohPartsImport.Suggest(["Drawing revision", "DWG #"])["drawingNumber"]);
    }

    [Fact]
    public async Task Part_master_import_keeps_the_drawing_number_whole_and_flags_a_revision_conflict()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var k = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var number = $"BWS{k}A";
        var csv = $"OR #,DWG #,Drawing revision\nPN-{k},{number},C\nPN-{k}B,BWS{k}B,\n";
        var read = await ReadAsync(eng, csv);
        var preview = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(read, site, false))).JsonAsync();
        Assert.Equal(2, preview.Int("add"));
        Assert.Equal(0, preview.Int("errors"));
        Assert.Equal(HttpStatusCode.OK, (await eng.PostAsync("/api/sequfloh/parts/import", Request(read, site, false))).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var part = await db.Parts.SingleAsync(p => p.PartNumber == $"PN-{k}");
            Assert.Equal(number, part.DrawingNumber);
            Assert.Equal("C", part.DrawingRevision);
            Assert.Equal("A", part.Revision);
            var bare = await db.Parts.SingleAsync(p => p.PartNumber == $"PN-{k}B");
            Assert.Equal($"BWS{k}B", bare.DrawingNumber);
            Assert.Null(bare.DrawingRevision);
        }

        var clash = $"OR #,DWG #,Drawing revision\nPN-{k},{number},D\n";
        var again = await ReadAsync(eng, clash);
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import/preview", Request(again, site, true))).JsonAsync();
        Assert.Equal(1, plan.Int("held"));
        Assert.Equal(0, plan.Int("update"));
        Assert.Equal(0, plan.Int("errors"));
        Assert.Contains("already recorded", plan.GetProperty("rows")[0].GetProperty("messages")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/sequfloh/parts/import", Request(again, site, true))).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("C", (await db.Parts.SingleAsync(p => p.PartNumber == $"PN-{k}")).DrawingRevision);
        }
    }

    [Fact]
    public async Task A_work_order_keeps_the_revision_in_effect_and_an_open_order_shows_a_newer_one()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"R{tag}", label = $"Rev family {tag}", isActive = true, isDefault = false })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = $"r{tag}" })).IsSuccessStatusCode);
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var number = $"BWS{tag}A";
        Guid partId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var part = new Part { PartNumber = $"PN-{tag}", Name = "Example sensor", Revision = "A", UnitOfMeasure = "Each", SiteId = site, TypeId = fam, DrawingNumber = number, DrawingRevision = "C" };
            db.Parts.Add(part);
            await db.SaveChangesAsync();
            partId = part.Id;
        }
        var folder = (await (await c.PostOkAsync($"{D}/folders", new { name = "Rev folder " + tag, siteId = site })).JsonAsync()).Id();
        var wrong = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(Pdf("wrong"), $"{number} rev Z.pdf", ("title", "Sensor"), ("drawingNumber", number), ("revision", "A"), ("partIds", partId.ToString())));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("Part Master", await wrong.Content.ReadAsStringAsync());

        var up = await c.Http.PostAsync($"{D}/folders/{folder}/drawings", Form(Pdf("rev c"), $"{number} rev Z.pdf", ("title", "Sensor"), ("drawingNumber", number), ("revision", "C"), ("partIds", partId.ToString())));
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var drawingId = (await up.JsonAsync()).Id();
        var detail = await c.GetElAsync($"{D}/{drawingId}");
        Assert.Equal(number, detail.Str("drawingNumber"));
        Assert.Equal("C", detail.Str("currentRevision"));
        Assert.Equal("C", detail.GetProperty("revisions")[0].Str("revision"));

        var order = await (await c.PostOkAsync("/api/sequfloh/tracking", new { type = "Test", siteId = site, partId, familyId = fam, number = (string?)null })).JsonAsync();
        var recordId = order.Id();
        var attached = (await c.GetElAsync($"{D}/for-record/{recordId}")).EnumerateArray().Single();
        Assert.Equal("C", attached.Str("attachedRevision"));
        Assert.Equal(number, attached.Str("attachedDrawingNumber"));
        Assert.False(attached.GetProperty("newerRevision").GetBoolean());

        var next = await c.Http.PostAsync($"{D}/{drawingId}/revisions", Form(Pdf("rev d"), $"{number} rev D.pdf", ("revision", "D"), ("reason", "Slot moved"), ("changeReference", "ECN-44")));
        Assert.True(next.IsSuccessStatusCode, await next.Content.ReadAsStringAsync());
        var after = (await c.GetElAsync($"{D}/for-record/{recordId}")).EnumerateArray().Single();
        Assert.Equal("C", after.Str("attachedRevision"));
        Assert.Equal("D", after.Str("currentRevision"));
        Assert.True(after.GetProperty("newerRevision").GetBoolean());
        Assert.Contains("rev c", await (await c.GetAsync($"{D}/{drawingId}/file?revision=C")).Content.ReadAsStringAsync());
        Assert.Contains((await c.GetElAsync($"/api/sequfloh/tracking?folder=open&q=PN-{tag}")).GetProperty("items").EnumerateArray(), x => x.Id() == recordId && x.GetProperty("drawingNewer").GetBoolean());

        using var check = app.Services.CreateScope();
        var saved = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var uploaded = await saved.AuditEntries.Where(a => a.EntityId == drawingId && a.Action == "Uploaded revision").OrderBy(a => a.OccurredAt).FirstAsync();
        Assert.Contains("Part Master", uploaded.Description);
        Assert.False(string.IsNullOrEmpty(uploaded.UserName));
        var audit = await saved.AuditEntries.Where(a => a.EntityId == recordId && a.Action == "DrawingRevisionRecorded").SingleAsync();
        Assert.False(string.IsNullOrEmpty(audit.UserName));
        Assert.Contains(number, audit.Description);
        Assert.Contains("revision C", audit.Description);
        Assert.Contains("does not change", audit.Description);
        var file = await saved.DrawingRevisions.SingleAsync(v => v.DrawingId == drawingId && v.Revision == "C");
        file.Sha256 = new string('a', 64);
        await Assert.ThrowsAsync<AppException>(() => saved.SaveChangesAsync());
    }

    [Fact]
    public async Task Folder_import_rejects_traversal_and_does_not_overwrite_a_different_file()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Import " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();
        var plan = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = false, files = new[] { new { path = "../escape.pdf", size = 12L }, new { path = "Sensors/plate.pdf", size = 20L } }, folders = new[] { "../away", "Sensors" } })).JsonAsync();
        var files = plan.GetProperty("files").EnumerateArray().ToList();
        Assert.True(files.Single(f => f.Str("path") == "../escape.pdf").GetProperty("rejected").GetBoolean());
        Assert.False(files.Single(f => f.Str("path") == "Sensors/plate.pdf").GetProperty("rejected").GetBoolean());
        Assert.Contains(plan.GetProperty("folders").EnumerateArray(), f => f.Str("path") == "../away" && f.GetProperty("rejected").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{I}/folders", new { folderId = root, linkParts = false, paths = new[] { "../away" } })).StatusCode);

        var bytes = Pdf("original plate");
        var ids = (await (await c.PostOkAsync($"{I}/folders", new { folderId = root, linkParts = false, paths = new[] { "Sensors" } })).JsonAsync()).EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetGuid());
        var imported = await ImportAsync(c, ids["Sensors"], "plate.pdf", bytes);
        Assert.False(imported.GetProperty("skipped").GetBoolean());
        var again = await (await c.PostOkAsync($"{I}/plan", new { folderId = root, linkParts = false, files = new[] { new { path = "Sensors/plate.pdf", size = bytes.LongLength + 8 } }, folders = Array.Empty<string>() })).JsonAsync();
        Assert.True(again.GetProperty("files")[0].GetProperty("conflict").GetBoolean());
        Assert.False(again.GetProperty("files")[0].GetProperty("exists").GetBoolean());

        var start = await (await c.PostOkAsync($"{I}/start", new { folderId = ids["Sensors"], fileName = "plate.pdf", size = Pdf("changed plate").LongLength })).JsonAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PutAsync(start.Str("url"), new ByteArrayContent(Pdf("changed plate")))).StatusCode);
        var clash = await c.PostAsync($"{I}/finish", new { token = start.Str("token"), revision = "A", linkParts = false, matchNames = new[] { "Sensors" } });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("Nothing was overwritten", await clash.Content.ReadAsStringAsync());
        Assert.Equal(bytes, await (await c.GetAsync($"{D}/{imported.Id()}/file")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Bulk_import_uses_the_part_master_revision_instead_of_the_typed_fallback()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parts.Add(new Part { PartNumber = $"SN-{tag}", Name = "Example sensor", Revision = "A", UnitOfMeasure = "Each", SiteId = site, DrawingNumber = $"SN-{tag}", DrawingRevision = "C" });
            await db.SaveChangesAsync();
        }
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Master " + tag, siteId = site })).JsonAsync()).Id();
        var done = await ImportAsync(c, root, $"SN-{tag}.pdf", Pdf("from master"), "A", [$"SN-{tag}"]);
        var drawing = await c.GetElAsync($"{D}/{done.Id()}");
        Assert.Equal("C", drawing.Str("currentRevision"));
        Assert.Contains("Part Master", drawing.GetProperty("revisions")[0].Str("reason"));
    }

    [Fact]
    public void Bulk_import_uses_the_typed_revision_when_the_part_master_revision_cannot_be_stored()
    {
        var invalid = DrawingRevisionRules.Resolve("A", ["N/A"], "3212-0226-SH51807", partMasterWins: true);
        Assert.Equal("A", invalid.Revision);
        Assert.False(invalid.FromPartMaster);
        Assert.Equal("The Part Master revision 'N/A' is not a usable revision, so the revision entered for this import was used. The Part Master was not changed.", invalid.Note);

        var several = DrawingRevisionRules.Resolve("A", ["B", "C"], "BWS4401A", partMasterWins: true);
        Assert.Equal("A", several.Revision);
        Assert.False(several.FromPartMaster);
        Assert.Equal("The Part Master records more than one revision (B, C), so the revision entered for this import was used. The Part Master was not changed.", several.Note);

        var usable = DrawingRevisionRules.Resolve("A", ["C"], "BWS4401A", partMasterWins: true);
        Assert.Equal("C", usable.Revision);
        Assert.True(usable.FromPartMaster);
        Assert.Null(usable.Note);

        Assert.Contains("Use letters, numbers", Assert.Throws<AppException>(() => DrawingRevisionRules.Resolve("A", ["N/A"], "3212-0226-SH51807", partMasterWins: false)).Message);
        Assert.Contains("more than one revision", Assert.Throws<AppException>(() => DrawingRevisionRules.Resolve("A", ["B", "C"], "BWS4401A", partMasterWins: false)).Message);

        var noTyped = DrawingRevisionRules.Resolve("", ["N/A"], "3212-0226-SH51807", partMasterWins: true);
        Assert.Equal("A", noTyped.Revision);
        Assert.False(noTyped.FromPartMaster);
        Assert.Contains("no revision was entered", noTyped.Note);
        Assert.Contains("so A was used", noTyped.Note);
        Assert.Contains("The Part Master was not changed", noTyped.Note);

        foreach (var raw in new[] { "A (2)", "REV A", "A/B", new string('X', 21) })
        {
            var fallback = DrawingRevisionRules.Resolve("A", [raw], "1214-0687", partMasterWins: true);
            Assert.Equal("A", fallback.Revision);
            Assert.Contains(raw, fallback.Note);
            Assert.Contains("not a usable revision", fallback.Note);

            var badTyped = DrawingRevisionRules.Resolve(raw, [raw], "1214-0687", partMasterWins: true);
            Assert.Equal("A", badTyped.Revision);
            Assert.Contains($"the revision entered for this import ('{raw}') is not a usable revision", badTyped.Note);
            Assert.Contains("so A was used", badTyped.Note);
        }
    }

    [Fact]
    public async Task Bulk_import_keeps_the_typed_revision_when_the_part_master_value_is_not_usable()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var number = $"3212-{tag}";
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Parts.Add(new Part { PartNumber = number, Name = "Example shock", Revision = "A", UnitOfMeasure = "Each", SiteId = site, DrawingNumber = number, DrawingRevision = "N/A" });
            db.Parts.Add(new Part { PartNumber = $"SN-{tag}B", Name = "Example sensor B", Revision = "A", UnitOfMeasure = "Each", SiteId = site, DrawingNumber = $"SN-{tag}", DrawingRevision = "B" });
            db.Parts.Add(new Part { PartNumber = $"SN-{tag}C", Name = "Example sensor C", Revision = "A", UnitOfMeasure = "Each", SiteId = site, DrawingNumber = $"SN-{tag}", DrawingRevision = "C" });
            await db.SaveChangesAsync();
        }
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Fallback " + tag, siteId = site })).JsonAsync()).Id();

        var unusable = await ImportAsync(c, root, $"{number}-SH51807.pdf", Pdf("unusable master"), "A", [number]);
        var unusableDrawing = await c.GetElAsync($"{D}/{unusable.Id()}");
        Assert.Equal("A", unusableDrawing.Str("currentRevision"));
        var unusableRev = unusableDrawing.GetProperty("revisions")[0];
        Assert.Equal("A", unusableRev.Str("revision"));
        Assert.Contains("N/A", unusableRev.Str("notes"));
        Assert.Contains("not a usable revision", unusableRev.Str("notes"));
        Assert.Contains("The Part Master was not changed", unusableRev.Str("notes"));
        Assert.Contains("The Part Master was not changed", unusableRev.Str("reason"));

        var clash = await ImportAsync(c, root, $"SN-{tag}.pdf", Pdf("several masters"), "A", [$"SN-{tag}B", $"SN-{tag}C"]);
        var clashDrawing = await c.GetElAsync($"{D}/{clash.Id()}");
        Assert.Equal("A", clashDrawing.Str("currentRevision"));
        var clashRev = clashDrawing.GetProperty("revisions")[0];
        Assert.Contains("more than one revision", clashRev.Str("notes"));
        Assert.Contains("The Part Master was not changed", clashRev.Str("notes"));
        Assert.Contains("The Part Master was not changed", clashRev.Str("reason"));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("N/A", (await db.Parts.SingleAsync(p => p.PartNumber == number)).DrawingRevision);
            Assert.Equal("B", (await db.Parts.SingleAsync(p => p.PartNumber == $"SN-{tag}B")).DrawingRevision);
            Assert.Equal("C", (await db.Parts.SingleAsync(p => p.PartNumber == $"SN-{tag}C")).DrawingRevision);
            var unusableAudit = await db.AuditEntries.SingleAsync(a => a.EntityId == unusable.Id() && a.Action == "Uploaded revision");
            Assert.False(string.IsNullOrEmpty(unusableAudit.UserName));
            Assert.Contains("N/A", unusableAudit.Description);
            Assert.Contains("not a usable revision", unusableAudit.Description);
            Assert.Contains("The Part Master was not changed", unusableAudit.Description);
            var clashAudit = await db.AuditEntries.SingleAsync(a => a.EntityId == clash.Id() && a.Action == "Uploaded revision");
            Assert.False(string.IsNullOrEmpty(clashAudit.UserName));
            Assert.Contains("more than one revision", clashAudit.Description);
            Assert.Contains("The Part Master was not changed", clashAudit.Description);
        }

        var stillRejected = await c.Http.PostAsync($"{D}/folders/{root}/drawings", Form(Pdf("single upload"), $"{number} single.pdf", ("title", "Shock"), ("drawingNumber", number), ("revision", "A")));
        Assert.Equal(HttpStatusCode.BadRequest, stillRejected.StatusCode);
        Assert.Contains("Use letters, numbers", await stillRejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bulk_import_finish_stores_a_usable_revision_when_the_part_master_value_cannot_be_stored_and_parts_are_not_linked()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var cases = new (string Number, string File, string Master, string Typed)[]
        {
            ($"3213-{tag}", $"3213-{tag} drawing.pdf", "A (2)", "A"),
            ($"1214-{tag}A", $"Revised 1214-{tag}A drawing.pdf", "REV A", "A"),
            ($"1214-{tag}B", $"1214-{tag}B.pdf", "A/B", "REV A"),
            ($"1214-{tag}C", $"1214-{tag}C.pdf", "SEE DWG A/B REV 2", "A (2)"),
        };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var row in cases)
                db.Parts.Add(new Part { PartNumber = row.Number, Name = "Example shock", Revision = "A", UnitOfMeasure = "Each", SiteId = site, DrawingNumber = row.Number, DrawingRevision = row.Master });
            await db.SaveChangesAsync();
        }
        var root = (await (await c.PostOkAsync($"{D}/folders", new { name = "Turkey " + tag, siteId = site })).JsonAsync()).Id();
        var leaf = (await (await c.PostOkAsync($"{D}/folders", new { name = "3213-0327", parentId = root, siteId = site })).JsonAsync()).Id();

        foreach (var row in cases)
        {
            var matchNames = new[] { "Shock Drawings", "SENSEN Drawings", "Turkey Drawings", row.Number };
            var done = await ImportAsync(c, leaf, row.File, Pdf(row.Number), row.Typed, matchNames, linkParts: false);
            Assert.False(done.GetProperty("skipped").GetBoolean());
            Assert.Equal(0, done.Int("linked"));
            var drawing = await c.GetElAsync($"{D}/{done.Id()}");
            Assert.Equal("A", drawing.Str("currentRevision"));
            var rev = drawing.GetProperty("revisions")[0];
            Assert.Equal("A", rev.Str("revision"));
            Assert.Contains(row.Master, rev.Str("notes"));
            Assert.Contains("The Part Master was not changed", rev.Str("notes"));
            Assert.Contains("The Part Master was not changed", rev.Str("reason"));
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(row.Master, (await db.Parts.SingleAsync(p => p.PartNumber == row.Number)).DrawingRevision);
            var audit = await db.AuditEntries.SingleAsync(a => a.EntityId == done.Id() && a.Action == "Uploaded revision");
            Assert.False(string.IsNullOrEmpty(audit.UserName));
            Assert.Contains(row.Master, audit.Description);
            Assert.Contains("The Part Master was not changed", audit.Description);
        }
    }

    static MultipartFormDataContent Form(byte[] bytes, string fileName, params (string Key, string Value)[] fields)
    {
        var f = new MultipartFormDataContent();
        f.Add(new ByteArrayContent(bytes), "file", fileName);
        foreach (var (k, v) in fields) f.Add(new StringContent(v), k);
        return f;
    }

    static async Task<JsonElement> ReadAsync(ApiClient c, string csv)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "parts.csv");
        var r = await c.Http.PostAsync("/api/sequfloh/parts/import/read", form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    static object Request(JsonElement t, Guid site, bool update) => new
    {
        fileName = t.GetProperty("fileName").GetString(),
        headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = t.GetProperty("mapping"), siteId = site, updateExisting = update,
        @fixed = new Dictionary<string, string> { ["name"] = "Fuel Pump" },
    };

    static async Task<JsonElement> ImportAsync(ApiClient c, Guid folder, string name, byte[] bytes, string revision = "A", string[]? matchNames = null, bool linkParts = true)
    {
        var s = await (await c.PostOkAsync($"{I}/start", new { folderId = folder, fileName = name, size = bytes.LongLength })).JsonAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(bytes))).StatusCode);
        var r = await c.PostOkAsync($"{I}/finish", new { token = s.Str("token"), revision, linkParts, matchNames = matchNames ?? Array.Empty<string>() });
        return await r.JsonAsync();
    }
}
