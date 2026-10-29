using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Findings Yes/No, job notes and pictures and files on a work order. Made-up data only.</summary>
[Collection("api")]
public sealed class JobNotesTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewReports, Permissions.SequflohCompleteTask];

    async Task<(Guid Id, ApiClient C, Guid Site)> OrderAsync(string[]? extra = null)
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([.. Tech, .. extra ?? []], site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        return (id, c, site);
    }

    async Task<List<(string Action, string Description)>> AuditsAsync(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id).Select(a => new { a.Action, a.Description }).ToListAsync()).Select(a => (a.Action, a.Description ?? "")).ToList();
    }

    [Fact]
    public async Task Findings_answer_defaults_to_No_and_cannot_be_No_while_findings_are_filled_in()
    {
        var (id, c, _) = await OrderAsync();
        Assert.False((await c.GetElAsync($"{T}/{id}/job")).GetProperty("findings").GetProperty("hasFindings").GetBoolean());
        await c.PutAsync($"{T}/{id}/job/findings", new { hasFindings = true });
        await c.PutAsync($"{T}/{id}/job/findings", new { hasFindings = true, description = "Made-up cracked housing" });
        var no = await c.PutAsync($"{T}/{id}/job/findings", new { hasFindings = false, description = "Made-up cracked housing" });
        Assert.Equal(HttpStatusCode.BadRequest, no.StatusCode);
        Assert.Contains("Clear them first", await no.Content.ReadAsStringAsync());
        var f = (await c.GetElAsync($"{T}/{id}/job")).GetProperty("findings");
        Assert.True(f.GetProperty("hasFindings").GetBoolean());
        Assert.Contains(await AuditsAsync(id), a => a.Action == "FindingsAnswerChanged" && a.Description.Contains("answered Yes"));
    }

    [Fact]
    public async Task Job_notes_are_newest_first_on_the_report_and_edits_keep_the_old_text_in_the_audit()
    {
        var (id, c, _) = await OrderAsync();
        await c.PostOkAsync($"{T}/{id}/job/notes", new { text = "Made-up: half the lot sorted" });
        await Task.Delay(20);
        await c.PostOkAsync($"{T}/{id}/job/notes", new { text = "Made-up: rest of the lot left on rack B" });
        var notes = (await c.GetElAsync($"{T}/{id}/job/notes")).EnumerateArray().ToList();
        Assert.Equal("Made-up: rest of the lot left on rack B", notes[0].Str("text"));
        Assert.True(notes[0].GetProperty("mine").GetBoolean());
        Assert.False(string.IsNullOrEmpty(notes[0].Str("authorName")));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{id}/job/notes", new { text = "  " })).StatusCode);

        var (_, other) = await app.CreateUserAsync(Tech);
        Assert.NotEqual(HttpStatusCode.NoContent, (await other.PutAsync($"{T}/job/notes/{notes[1].Id()}", new { text = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsync($"{T}/job/notes/{notes[1].Id()}", new { text = "Made-up: half the lot sorted and boxed" })).StatusCode);
        var audits = await AuditsAsync(id);
        Assert.Contains(audits, a => a.Action == "JobNoteEdited" && a.Description.Contains("Was: \"Made-up: half the lot sorted\""));
        Assert.Equal(2, audits.Count(a => a.Action == "JobNoteAdded"));

        var rep = await c.GetElAsync($"{T}/{id}/job/report");
        Assert.Equal(2, rep.GetProperty("notes").GetArrayLength());
        var pdf = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/job/report/pdf")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Job notes", pdf);
    }

    static MultipartFormDataContent Form(string name, string content)
    {
        var form = new MultipartFormDataContent();
        var b = new ByteArrayContent(Encoding.UTF8.GetBytes(content)); b.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(b, "files", name); form.Add(new StringContent("attachment"), "kind");
        return form;
    }

    [Fact]
    public async Task Files_upload_is_audited_and_removing_needs_the_permission_and_keeps_history()
    {
        var (id, c, site) = await OrderAsync();
        var up = await c.Http.PostAsync($"{T}/{id}/job/test-files", Form("made-up-drawing.pdf", "%PDF-1.4 made up"));
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var fileId = (await up.JsonAsync()).EnumerateArray().First().Id();
        Assert.Contains(await AuditsAsync(id), a => a.Action == "FileUploaded" && a.Description.Contains("made-up-drawing.pdf"));

        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"{T}/job/test-files/{fileId}")).StatusCode);
        var (_, boss) = await app.CreateUserAsync([.. Tech, Permissions.SequflohRemoveFiles], site);
        Assert.Equal(HttpStatusCode.NoContent, (await boss.DeleteAsync($"{T}/job/test-files/{fileId}?reason=Made-up%20wrong%20order")).StatusCode);
        var job = await c.GetElAsync($"{T}/{id}/job");
        Assert.Equal(0, job.GetProperty("files").GetArrayLength());
        var removed = job.GetProperty("removedFiles").EnumerateArray().Single();
        Assert.Equal("Made-up wrong order", removed.Str("removeReason"));
        Assert.True((await c.GetAsync($"{T}/job/test-files/{fileId}")).IsSuccessStatusCode);
        Assert.Contains(await AuditsAsync(id), a => a.Action == "FileRemoved" && a.Description.Contains("Reason: Made-up wrong order"));
    }
}
