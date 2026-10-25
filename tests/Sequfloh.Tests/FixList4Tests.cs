using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Record numbers and file names are the company's own: typed by the user, editable later, never made up by the app.</summary>
[Collection("api")]
public sealed class FixList4Tests(TestApp app)
{
    static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Sends a request exactly as written (the test helper does not add a made-up number).</summary>
    static async Task<HttpResponseMessage> Own(ApiClient c, HttpMethod method, string url, object body)
    {
        var req = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Test-Own-Number", "1");
        return await c.Http.SendAsync(req);
    }

    static async Task<JsonElement> Upload(ApiClient c, Guid site, string fileName, string? number = null, bool blank = false)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ImportTests.InspectionWorkbook()), "file", fileName);
        form.Add(new StringContent(site.ToString()), "siteId");
        if (number is not null) form.Add(new StringContent(number), "number");
        if (blank) form.Add(new StringContent("true"), "blankForm");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/imported-files") { Content = form };
        req.Headers.Add("X-Test-Own-Number", "1");
        var res = await c.Http.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    [Fact]
    public async Task An_imported_file_keeps_its_saved_file_name_as_its_number()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var name = $"QF-0457 rev C Torque_{Tag()}";
        var f = await Upload(editor, site, name + ".xlsx");
        Assert.Equal(name, f.Str("number")); // nothing appended or prefixed, extension dropped
        Assert.Equal(name, f.Str("title"));
        // the same file name again within imported files is blocked, whatever the case
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(ImportTests.InspectionWorkbook()), "file", name.ToUpperInvariant() + ".xlsx");
        form.Add(new StringContent(site.ToString()), "siteId");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/imported-files") { Content = form };
        req.Headers.Add("X-Test-Own-Number", "1");
        var dup = await editor.Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Fill_out_and_save_as_copy_need_a_number_typed_by_the_user()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var src = await Upload(editor, site, $"Blank form {Tag()}.xlsx", blank: true);
        var noNumber = await Own(editor, HttpMethod.Post, $"/api/imported-files/{src.Id()}/fill-out", new { title = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, noNumber.StatusCode);
        var why = await noNumber.Content.ReadAsStringAsync(); Assert.True(why.Contains("Enter the"), why);
        var own = $"QF-FILL-{Tag()}";
        var filled = await (await Own(editor, HttpMethod.Post, $"/api/imported-files/{src.Id()}/fill-out", new { number = own })).JsonAsync();
        Assert.Equal(own, filled.Str("number"));
        var copyNoNumber = await Own(editor, HttpMethod.Post, $"/api/imported-files/{filled.Id()}/save-as-copy", new { baseVersion = 1, title = "Retest", cells = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, copyNoNumber.StatusCode);
    }

    [Fact]
    public async Task No_create_path_makes_up_a_number_and_number_sequences_are_left_alone()
    {
        var site = await app.CreateSiteAsync();
        var (_, u) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManagePartsLists, Permissions.SequflohManageOperationSteps,
            Permissions.SequflohCreateWorkInstruction, Permissions.SequflohManageChargebacks], site);
        List<(string, long)> before;
        using (var scope = app.Services.CreateScope())
            before = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().NumberSequences.AsNoTracking().ToListAsync()).Select(s => (s.RecordType, (long)s.NextValue)).OrderBy(x => x.Item1).ToList();

        var withoutNumber = new (string Url, object Body)[]
        {
            ("/api/sequfloh/parts-lists", new { title = "Kit", listType = "Standard" }),
            ("/api/sequfloh/operation-steps", new { title = "Torque", detailedAction = "Torque bolts", isActive = true }),
            ("/api/sequfloh/instructions", new { type = "Validation", title = "Leak test", siteId = site }),
            ("/api/sequfloh/inspection-requests", new { siteId = site, partNumber = "PN-1", priority = "Normal" }),
            ("/api/sequfloh/tracking", new { type = "Production", siteId = site }),
            ("/api/sequfloh/chargebacks", new { sections = new[] { "sumHours" }, period = new { kind = "Week", from = "2001-02-14" } }),
        };
        foreach (var (url, body) in withoutNumber)
        {
            var r = await Own(u, HttpMethod.Post, url, body);
            Assert.True(r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{url}: {r.StatusCode}");
            if (r.StatusCode == HttpStatusCode.BadRequest) Assert.Contains("Enter the", await r.Content.ReadAsStringAsync());
        }
        var own = $"pl 0457/{Tag()} rev-A";
        var list = await (await Own(u, HttpMethod.Post, "/api/sequfloh/parts-lists", new { number = own, title = "Kit", listType = "Standard" })).JsonAsync();
        Assert.Equal(own, list.Str("number")); // exactly as typed: no format rules
        var wi = await (await Own(u, HttpMethod.Post, "/api/sequfloh/instructions", new { number = "WI " + own, type = "Validation", title = "Leak test", siteId = site })).JsonAsync();
        Assert.Equal("WI " + own, wi.Str("number"));

        using (var scope = app.Services.CreateScope())
        {
            var after = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().NumberSequences.AsNoTracking().ToListAsync()).Select(s => (s.RecordType, (long)s.NextValue)).OrderBy(x => x.Item1).ToList();
            Assert.Equal(before, after);
        }
    }

    [Fact]
    public async Task A_duplicate_number_is_blocked_within_a_type_regardless_of_case_but_allowed_across_types()
    {
        var (_, u) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManagePartsLists, Permissions.SequflohManageOperationSteps]);
        var n = $"ABC-{Tag()}";
        Assert.True((await Own(u, HttpMethod.Post, "/api/sequfloh/parts-lists", new { number = n, title = "Kit", listType = "Standard" })).IsSuccessStatusCode);
        var dup = await Own(u, HttpMethod.Post, "/api/sequfloh/parts-lists", new { number = n.ToLowerInvariant(), title = "Kit 2", listType = "Standard" });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
        Assert.Contains("already exists", await dup.Content.ReadAsStringAsync());
        // another type may use the same number
        Assert.True((await Own(u, HttpMethod.Post, "/api/sequfloh/operation-steps", new { number = n, title = "Torque", detailedAction = "Torque bolts", isActive = true })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Number_and_title_are_editable_afterwards_and_every_change_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, u) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManagePartsLists, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var first = $"PL-OLD-{Tag()}"; var other = $"PL-OTHER-{Tag()}";
        var list = await (await Own(u, HttpMethod.Post, "/api/sequfloh/parts-lists", new { number = first, title = "Old title", listType = "Standard" })).JsonAsync();
        await Own(u, HttpMethod.Post, "/api/sequfloh/parts-lists", new { number = other, title = "Other", listType = "Standard" });
        var url = $"/api/sequfloh/records/parts-list/{list.Id()}/number";

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(url, new { number = "X", title = "Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await u.PutAsync(url, new { number = other.ToUpperInvariant().Replace("PL-", "pl-"), title = "Y" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await u.PutAsync(url, new { number = "  ", title = "Y" })).StatusCode);
        var renamed = $"4410-PL/{Tag()}";
        Assert.True((await u.PutAsync(url, new { number = renamed, title = "New title" })).IsSuccessStatusCode);
        var got = (await u.GetElAsync($"/api/sequfloh/parts-lists/{list.Id()}")).GetProperty("list");
        Assert.Equal(renamed, got.Str("number")); Assert.Equal("New title", got.Str("title"));

        var file = await Upload(u, site, $"QF-77 {Tag()}.xlsx");
        var fileName = $"QF-77 renamed {Tag()}";
        Assert.True((await u.PutAsync($"/api/sequfloh/records/file/{file.Id()}/number", new { number = fileName, title = "Torque sheet" })).IsSuccessStatusCode);
        Assert.Equal(fileName, (await u.GetElAsync($"/api/imported-files/{file.Id()}")).Str("number"));
        var dl = await u.Http.GetAsync($"/api/imported-files/{file.Id()}/download");
        Assert.Equal(fileName + ".xlsx", dl.Content.Headers.ContentDisposition!.FileNameStar ?? dl.Content.Headers.ContentDisposition.FileName!.Trim('"')); // downloads carry the user's name

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = await db.AuditEntries.AsNoTracking().SingleAsync(e => e.EntityId == list.Id() && e.Action == "Changed number");
        Assert.Contains(first, a.Description); Assert.Contains(renamed, a.Description);
        Assert.Contains("Old title", a.Changes!); Assert.Contains("New title", a.Changes!);
        Assert.False(string.IsNullOrEmpty(a.UserName));
        Assert.True(await db.AuditEntries.AnyAsync(e => e.EntityId == file.Id() && e.Action == "Changed number"));
    }

    const string T = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewLiveExecution, Permissions.SequflohViewReports];
    static List<Guid> Ids(JsonElement list) => list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task A_salt_spray_test_work_order_has_its_own_folder_type_filter_and_clock()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var id = (await (await c.PostOkAsync(T, new { type = "SaltSpray", siteId = site })).JsonAsync()).Id();
        var rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal("SaltSpray", rec.Str("type"));
        Assert.Equal([id], Ids(await c.GetElAsync($"{T}?folder=salt-spray&site={site}")));
        Assert.Equal([id], Ids(await c.GetElAsync($"{T}?folder=open&type=SaltSpray&site={site}")));
        // the same clock as every other work order
        await c.PostOkAsync($"{T}/{id}/time/start");
        await c.PostOkAsync($"{T}/{id}/time/stop");
        Assert.Single((await c.GetElAsync($"{T}/{id}/time")).GetProperty("people").EnumerateArray());
        // the report type filter offers it and labels it
        var rows = (await c.GetElAsync($"/api/sequfloh/reports/task-performance?type=SaltSpray&site={site}&pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.Str("type") == "Salt Spray Test");
    }

    [Fact]
    public async Task Validation_and_FAI_records_are_shown_and_counted_together_as_Validation_FAI()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var v = (await (await admin.PostOkAsync(T, new { type = "Validation", siteId = site })).JsonAsync()).Id();
        var fai = (await (await admin.PostOkAsync(T, new { type = "Fai", siteId = site })).JsonAsync()).Id();
        // stored types are kept as they are
        Assert.Equal("Fai", (await admin.GetElAsync($"{T}/{fai}")).GetProperty("record").Str("type"));
        foreach (var url in new[] { $"{T}?folder=validation-fai&site={site}", $"{T}?folder=fai&site={site}", $"{T}?folder=validation&site={site}", $"{T}?folder=open&type=Validation&site={site}", $"{T}?folder=open&type=Fai&site={site}" })
        {
            var ids = Ids(await admin.GetElAsync(url));
            Assert.Contains(v, ids); Assert.Contains(fai, ids);
        }
        // one Validation / FAI Status report; the old FAI report opens it
        var catalog = (await admin.GetElAsync("/api/sequfloh/reports")).GetProperty("reports").EnumerateArray().Select(x => x.Str("key")).ToList();
        Assert.Contains("validation", catalog); Assert.DoesNotContain("fai", catalog);
        foreach (var key in new[] { "validation", "fai" })
        {
            var r = await admin.GetElAsync($"/api/sequfloh/reports/{key}?site={site}");
            Assert.Equal("Validation / FAI Status", r.GetProperty("title").GetString());
            Assert.Equal(2, r.GetProperty("total").GetInt32());
        }
        var tp = (await admin.GetElAsync($"/api/sequfloh/reports/task-performance?type=Validation&site={site}&pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, tp.Count);
        Assert.All(tp, r => Assert.Equal("Validation / FAI", r.Str("type")));
    }
}
