using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Shared;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Tests;

/// <summary>
/// Deleting a chargeback report removes the report and a file the report itself stored.
/// The work orders it names, their photos, time, parts and drawings stay, and the project total drops by that report only.
/// </summary>
[Collection("api")]
public sealed class ChargebackDeleteTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    const string C = "/api/sequfloh/chargebacks";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohViewReports, Permissions.SequflohManageChargebacks, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageProjects];
    static readonly Regex When = new(@"on [A-Z][a-z]{2} \d{1,2}, \d{4} \d{1,2}:\d{2} [AP]M$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task Delete_and_bulk_delete_remove_only_the_reports()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var projectNo = $"PCB-{tag}";
        var aNo = $"34BW5083-{tag}";
        var bNo = $"34BW5084-{tag}";
        var reportNo = $"CB-{tag}";
        var pid = (await (await admin.PostOkAsync("/api/sequfloh/projects", new { number = projectNo, name = "Made-up chargeback project", siteId = site })).JsonAsync()).Id();
        async Task<Guid> Wo(string n) => (await (await admin.PostOkAsync(T, new { type = "Inspection", siteId = site, number = n, projectId = pid })).JsonAsync()).Id();
        var a = await Wo(aNo);
        var b = await Wo(bNo);
        var start = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        Guid partId, drawingId;
        string photoKey, drawingKey, reportKey;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var adminId = await db.Users.Where(u => u.Email == TestApp.AdminEmail).Select(u => u.Id).SingleAsync();
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = a, TrackingNumber = aNo, UserId = adminId, UserName = "Administrator", StartedAt = start, StoppedAt = start.AddHours(2), Seconds = 2 * 3600 });
            db.SequflohTimeEntries.Add(new SequflohTimeEntry { TrackingRecordId = b, TrackingNumber = bNo, UserId = adminId, UserName = "Administrator", StartedAt = start.AddDays(1), StoppedAt = start.AddDays(1).AddHours(3.5), Seconds = (long)(3.5 * 3600) });
            db.SequflohCrewSessions.Add(new SequflohCrewSession { TrackingRecordId = a, TrackingNumber = aNo, CrewSize = 2, StartedAt = start, StoppedAt = start.AddHours(1), Entered = true });
            var part = new Part { PartNumber = $"PN-{tag}", Name = "Fuel pump bracket", SiteId = site, Status = MasterStatus.Active };
            var folder = new DrawingFolder { Name = $"Folder {tag}", SiteId = site };
            var drawing = new Drawing { FolderId = folder.Id, Title = "Bracket drawing", DrawingNumber = $"DWG-{tag}", CurrentRevision = "A", SiteId = site };
            drawingKey = $"drawings/{drawing.Id:N}/a.pdf";
            var revision = new DrawingRevision { DrawingId = drawing.Id, Revision = "A", DrawingNumber = drawing.DrawingNumber, FileName = "bracket.pdf", ContentType = "application/pdf", StorageKey = drawingKey, Sha256 = "draw", Status = DrawingRevisionStatus.Current, CreatedByName = "Administrator" };
            db.Parts.Add(part);
            db.DrawingFolders.Add(folder);
            db.Drawings.Add(drawing);
            db.DrawingRevisions.Add(revision);
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = drawing.Id, PartId = part.Id, CreatedAt = start });
            db.TrackingRecordDrawings.Add(new TrackingRecordDrawing { TrackingRecordId = a, DrawingId = drawing.Id, AttachedRevision = "A", AttachedDrawingNumber = drawing.DrawingNumber, AttachedRevisionId = revision.Id, AttachedAt = start });
            db.SequflohSavedJobReports.Add(new SequflohSavedJobReport { TrackingRecordId = a, TrackingNumber = aNo, WorkOrderNumber = aNo, SiteId = site, Reason = "Saved", FileName = "job.pdf", Pdf = [1, 2, 3, 4], CreatedByName = "Administrator" });
            await db.SaveChangesAsync();
            partId = part.Id;
            drawingId = drawing.Id;
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            await storage.PutAsync(drawingKey, new MemoryStream("%PDF-drawing"u8.ToArray()), "application/pdf", CancellationToken.None);
        }
        await Upload(admin, a, "crack.jpg");
        await Upload(admin, b, "seal.png");

        var linked = await (await admin.PostOkAsync(C, new
        {
            workOrderNumber = reportNo, partDescription = "Fuel pump bracket", hourlyRate = 40m, qtyBad = 4,
            workOrderIds = new[] { a, b }, period = new { kind = "Project", projectId = pid },
        })).JsonAsync();
        var linkedId = linked.Id();
        Assert.Equal(projectNo, linked.GetProperty("projectNumber").GetString());
        var single = await (await admin.PostOkAsync(C, new { workOrderNumber = $"CB-ONE-{tag}", trackingRecordId = a, hourlyRate = 10m, partDescription = "Fuel pump bracket" })).JsonAsync();
        var singleId = single.Id();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var adminId = await db.Users.Where(u => u.Email == TestApp.AdminEmail).Select(u => u.Id).SingleAsync();
            reportKey = $"SequflohChargebackReport/{linkedId:N}/report.pdf";
            db.Attachments.Add(new Attachment
            {
                OwnerType = nameof(SequflohChargebackReport), OwnerId = linkedId, SiteId = site, FileName = "chargeback.pdf", ContentType = "application/pdf",
                SizeBytes = 12, Sha256 = "report", StorageKey = reportKey,
            });
            db.SequflohUserItems.Add(new SequflohUserItem { UserId = adminId, Kind = "recent", ItemType = "chargeback", RecordId = linkedId, Path = $"/sequfloh/chargebacks/{linkedId}", Label = reportNo, LastOpenedAt = start });
            db.SequflohUserItems.Add(new SequflohUserItem { UserId = adminId, Kind = "recent", ItemType = "work-order", RecordId = a, Path = $"/sequfloh/record/{a}", Label = aNo, LastOpenedAt = start });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IFileStorage>().PutAsync(reportKey, new MemoryStream("%PDF-report"u8.ToArray()), "application/pdf", CancellationToken.None);
            photoKey = await db.SequflohTestFiles.Where(f => f.TrackingRecordId == a).Select(f => f.StorageKey).SingleAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync($"{C}/{linkedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"{C}/delete", new { ids = new[] { linkedId, singleId } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"{C}/delete", new { ids = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{C}/delete", new { ids = new[] { singleId, Guid.NewGuid() } })).StatusCode);

        var before = await admin.GetElAsync($"/api/sequfloh/projects/{pid}");
        Assert.Equal(2, before.GetProperty("totals").GetProperty("chargebackReports").GetInt32());
        Assert.Equal(240m, before.GetProperty("totals").GetProperty("chargebackTotal").GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{C}/{linkedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{C}/{linkedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{C}/{linkedId}")).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            Assert.False(await db.SequflohChargebackReports.AnyAsync(c => c.Id == linkedId));
            Assert.False(await db.Attachments.AnyAsync(x => x.OwnerId == linkedId));
            Assert.Null(await storage.GetAsync(reportKey, CancellationToken.None));
            Assert.NotNull(await storage.GetAsync(photoKey, CancellationToken.None));
            Assert.NotNull(await storage.GetAsync(drawingKey, CancellationToken.None));
            Assert.Equal(2, await db.SequflohTimeEntries.CountAsync(e => e.TrackingRecordId == a || e.TrackingRecordId == b));
            Assert.Equal(1, await db.SequflohCrewSessions.CountAsync(e => e.TrackingRecordId == a));
            Assert.Equal(2, await db.SequflohTestFiles.CountAsync(f => f.TrackingRecordId == a || f.TrackingRecordId == b));
            Assert.True(await db.SequflohTrackingRecords.AnyAsync(r => r.Id == a && r.ProjectId == pid));
            Assert.True(await db.SequflohTrackingRecords.AnyAsync(r => r.Id == b && r.ProjectId == pid));
            Assert.True(await db.Parts.AnyAsync(p => p.Id == partId));
            Assert.True(await db.Drawings.AnyAsync(d => d.Id == drawingId));
            Assert.True(await db.DrawingRevisions.AnyAsync(r => r.DrawingId == drawingId));
            Assert.True(await db.DrawingPartLinks.AnyAsync(l => l.DrawingId == drawingId && l.PartId == partId));
            Assert.True(await db.TrackingRecordDrawings.AnyAsync(l => l.TrackingRecordId == a && l.DrawingId == drawingId));
            Assert.Equal(4, (await db.SequflohSavedJobReports.SingleAsync(r => r.TrackingRecordId == a)).Pdf.Length);
            Assert.False(await db.SequflohUserItems.AnyAsync(i => i.RecordId == linkedId));
            Assert.True(await db.SequflohUserItems.AnyAsync(i => i.RecordId == a));
            var audit = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.EntityId == linkedId && e.Action == "ChargebackReportDeleted");
            Assert.Equal("Administrator", audit.UserName);
            Assert.NotNull(audit.UserId);
            Assert.InRange(audit.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Contains($"chargeback report {reportNo} (Fuel pump bracket)", audit.Description);
            Assert.Contains($"project {projectNo} (Made-up chargeback project)", audit.Description);
            Assert.Contains($"work orders {aNo}, {bNo}", audit.Description);
            Assert.StartsWith("Administrator deleted ", audit.Description);
            Assert.Matches(When, audit.Description);
            Assert.DoesNotContain(linkedId.ToString(), audit.Description);
        }

        var afterOne = await admin.GetElAsync($"/api/sequfloh/projects/{pid}");
        Assert.Equal(1, afterOne.GetProperty("totals").GetProperty("chargebackReports").GetInt32());
        Assert.Equal(20m, afterOne.GetProperty("totals").GetProperty("chargebackTotal").GetDecimal());
        var summary = (await admin.GetElAsync("/api/sequfloh/projects/open-summary")).EnumerateArray().Single(p => p.GetProperty("number").GetString() == projectNo);
        Assert.Equal(20m, summary.GetProperty("totalCost").GetDecimal());

        var extra = await (await admin.PostOkAsync(C, new { workOrderNumber = $"CB-TWO-{tag}" })).JsonAsync();
        var extraId = extra.Id();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{C}/delete", new { ids = new[] { singleId, extraId } })).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.SequflohChargebackReports.AnyAsync(c => c.Id == singleId || c.Id == extraId));
            var audits = await db.AuditEntries.AsNoTracking().Where(e => e.Action == "ChargebackReportDeleted" && (e.EntityId == singleId || e.EntityId == extraId)).ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.Contains(audits, e => e.EntityId == singleId && e.Description.Contains($"chargeback report CB-ONE-{tag}") && e.Description.Contains($"work order {aNo}") && When.IsMatch(e.Description));
            Assert.Contains(audits, e => e.EntityId == extraId && e.Description.Contains($"chargeback report CB-TWO-{tag}") && e.Description.Contains($"work order CB-TWO-{tag}") && When.IsMatch(e.Description));
            Assert.Equal(2, await db.SequflohTimeEntries.CountAsync(e => e.TrackingRecordId == a || e.TrackingRecordId == b));
            Assert.Equal(2, await db.SequflohTestFiles.CountAsync(f => f.TrackingRecordId == a || f.TrackingRecordId == b));
        }
        var cleared = await admin.GetElAsync($"/api/sequfloh/projects/{pid}");
        Assert.Equal(0, cleared.GetProperty("totals").GetProperty("chargebackReports").GetInt32());
        Assert.Equal(0m, cleared.GetProperty("totals").GetProperty("chargebackTotal").GetDecimal());
        var home = (await admin.GetElAsync("/api/sequfloh/projects/open-summary")).EnumerateArray().Single(p => p.GetProperty("number").GetString() == projectNo);
        Assert.Equal(0m, home.GetProperty("totalCost").GetDecimal());
    }

    static async Task Upload(ApiClient client, Guid id, string name)
    {
        var bytes = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? new byte[] { 0x89, 0x50, 0x4E, 0x47 } : new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "files", name }, { new StringContent("evidence"), "kind" } };
        var sent = await client.Http.PostAsync($"{T}/{id}/job/test-files", form);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
    }
}
