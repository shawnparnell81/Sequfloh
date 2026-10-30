using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Delete permanently: work orders cancelled as "Created in error" and removed files. Made-up data only.</summary>
[Collection("api")]
public sealed class DeletePermanentlyTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCancelTask,
        Permissions.SequflohRemoveFiles, Permissions.SequflohDeleteRecords];
    static readonly string[] Admin = [.. Tech, Permissions.SequflohDeletePermanently];

    AppDbContext Db(out IServiceScope scope) { scope = app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<AppDbContext>(); }
    static async Task<int> Ver(ApiClient c, Guid id) => (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();

    static MultipartFormDataContent Form(string name)
    {
        var form = new MultipartFormDataContent();
        var b = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 made up")); b.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(b, "files", name); form.Add(new StringContent("attachment"), "kind");
        return form;
    }

    // a work order with time, a note and a file, cancelled with the given reason
    async Task<(Guid Id, string Number, ApiClient Admin, ApiClient Tech, Guid FileId)> CancelledAsync(string reason)
    {
        var site = await app.CreateSiteAsync();
        var (_, admin) = await app.CreateUserAsync(Admin, site);
        var (_, tech) = await app.CreateUserAsync(Tech, site);
        var id = (await (await admin.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        var up = await admin.Http.PostAsync($"{T}/{id}/job/test-files", Form("made-up-photo.pdf"));
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var fileId = (await up.JsonAsync()).EnumerateArray().First().Id();
        await admin.StartAsync(id);
        var c = await admin.PostAsync($"{T}/{id}/actions/cancel", new { operationId = Guid.NewGuid(), stateVersion = await Ver(admin, id), reason });
        Assert.True(c.IsSuccessStatusCode, await c.Content.ReadAsStringAsync());
        var number = (await admin.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number")!;
        return (id, number, admin, tech, fileId);
    }

    [Fact]
    public async Task Created_in_error_work_order_is_deleted_with_its_dependents_and_stored_files_and_the_audit_stays()
    {
        var (id, number, admin, tech, fileId) = await CancelledAsync("Created in error");
        string key; int auditsBefore;
        { var db = Db(out var s); using var _ = s;
          key = await db.SequflohTestFiles.IgnoreQueryFilters().Where(f => f.Id == fileId).Select(f => f.StorageKey).SingleAsync();
          Assert.True(await db.SequflohTimeEntries.IgnoreQueryFilters().AnyAsync(e => e.TrackingRecordId == id) || await db.SequflohTaskEvents.AnyAsync(e => e.TrackingRecordId == id));
          auditsBefore = await db.AuditEntries.CountAsync(a => a.EntityId == id); }
        Assert.True(File.Exists(Path.Combine(TestApp.FilesPath, key)));
        Assert.True((await admin.GetElAsync($"{T}/{id}")).GetProperty("canDeletePermanently").GetBoolean());
        Assert.False((await tech.GetElAsync($"{T}/{id}")).GetProperty("canDeletePermanently").GetBoolean());
        // the ordinary delete still refuses (time was logged)
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{T}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsync($"{T}/{id}/delete-permanently", new { confirmNumber = number })).StatusCode);
        var wrong = await admin.PostAsync($"{T}/{id}/delete-permanently", new { confirmNumber = "WRONG-1" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains($"Type the work order number {number}", await wrong.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{T}/{id}/delete-permanently", new { confirmNumber = number.ToLowerInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{T}/{id}")).StatusCode);
        Assert.False(File.Exists(Path.Combine(TestApp.FilesPath, key)));
        { var db = Db(out var s); using var _ = s;
          Assert.False(await db.SequflohTrackingRecords.IgnoreQueryFilters().AnyAsync(r => r.Id == id));
          Assert.False(await db.SequflohTestFiles.IgnoreQueryFilters().AnyAsync(e => e.TrackingRecordId == id));
          Assert.False(await db.SequflohTimeEntries.IgnoreQueryFilters().AnyAsync(e => e.TrackingRecordId == id));
          Assert.False(await db.SequflohTaskEvents.AnyAsync(e => e.TrackingRecordId == id));
          var audits = await db.AuditEntries.Where(a => a.EntityId == id).ToListAsync();
          Assert.True(audits.Count > auditsBefore); // nothing removed from the audit trail
          Assert.Contains(audits, a => a.Action == "Deleted" && a.Description.Contains("permanently") && a.Description.Contains("Created in error")); }
    }

    [Fact]
    public async Task Other_cancel_reasons_and_open_work_orders_cannot_be_deleted_permanently()
    {
        var (id, number, admin, _, _) = await CancelledAsync("Order canceled");
        Assert.False((await admin.GetElAsync($"{T}/{id}")).GetProperty("canDeletePermanently").GetBoolean());
        var r = await admin.PostAsync($"{T}/{id}/delete-permanently", new { confirmNumber = number });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Created in error", await r.Content.ReadAsStringAsync());
        var site = await app.CreateSiteAsync();
        var (_, a2) = await app.CreateUserAsync(Admin, site);
        var open = (await (await a2.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL" })).JsonAsync()).Id();
        var no = (await a2.GetElAsync($"{T}/{open}")).GetProperty("record").Str("number");
        Assert.Equal(HttpStatusCode.Conflict, (await a2.PostAsync($"{T}/{open}/delete-permanently", new { confirmNumber = no })).StatusCode);
    }

    [Fact]
    public async Task A_work_order_a_chargeback_refers_to_is_not_deleted()
    {
        var (id, number, admin, _, _) = await CancelledAsync("Created in error");
        { var db = Db(out var s); using var _ = s;
          db.SequflohChargebackReports.Add(new SequflohChargebackReport { WorkOrderNumber = "MADE-UP-CB", TrackingRecordId = id, TrackingNumber = number });
          await db.SaveChangesAsync(); }
        var r = await admin.PostAsync($"{T}/{id}/delete-permanently", new { confirmNumber = number });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("1 chargeback report", await r.Content.ReadAsStringAsync());
        Assert.True((await admin.GetAsync($"{T}/{id}")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Removed_files_can_be_deleted_permanently_with_the_permission()
    {
        var site = await app.CreateSiteAsync();
        var (_, admin) = await app.CreateUserAsync(Admin, site);
        var (_, tech) = await app.CreateUserAsync(Tech, site);
        var id = (await (await admin.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL" })).JsonAsync()).Id();
        var fileId = (await (await admin.Http.PostAsync($"{T}/{id}/job/test-files", Form("made-up-wrong.pdf"))).JsonAsync()).EnumerateArray().First().Id();
        string key; { var db = Db(out var s); using var _ = s; key = await db.SequflohTestFiles.Where(f => f.Id == fileId).Select(f => f.StorageKey).SingleAsync(); }
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"{T}/job/test-files/{fileId}/permanently")).StatusCode); // not removed yet
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{T}/job/test-files/{fileId}?reason=Made-up%20mistake")).StatusCode);
        Assert.True((await admin.GetElAsync($"{T}/{id}/job")).GetProperty("canDeleteFilesPermanently").GetBoolean());
        Assert.False((await tech.GetElAsync($"{T}/{id}/job")).GetProperty("canDeleteFilesPermanently").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.DeleteAsync($"{T}/job/test-files/{fileId}/permanently")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{T}/job/test-files/{fileId}/permanently")).StatusCode);
        Assert.Equal(0, (await admin.GetElAsync($"{T}/{id}/job")).GetProperty("removedFiles").GetArrayLength());
        Assert.False(File.Exists(Path.Combine(TestApp.FilesPath, key)));
        var db2 = Db(out var s2); using var __ = s2;
        Assert.Contains(await db2.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action + "|" + a.Description).ToListAsync(),
            a => a.StartsWith("FileDeletedPermanently|") && a.Contains("made-up-wrong.pdf"));
        Assert.Contains(await db2.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Action).ToListAsync(), a => a == "FileRemoved"); // earlier history kept
    }
}
