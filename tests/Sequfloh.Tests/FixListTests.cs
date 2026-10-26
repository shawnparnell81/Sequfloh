using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Work orders without an instruction, Procedure Instructions, exact drawing matching and not-applicable part fields. Made-up data only.</summary>
[Collection("api")]
public sealed class FixListTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask];
    static readonly string[] Author = [Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction, Permissions.SequflohReviseWorkInstruction];
    static readonly string[] Drawings = [Permissions.SequflohAccess, Permissions.SequflohViewDrawings, Permissions.SequflohManageDrawings];

    async Task<AppDbContext> Db(IServiceScope s) { await Task.CompletedTask; return s.ServiceProvider.GetRequiredService<AppDbContext>(); }

    async Task<Guid> ReleasedAsync(Guid site, WorkInstructionType type = WorkInstructionType.Validation)
    {
        using var scope = app.Services.CreateScope();
        var db = await Db(scope);
        var wi = new SequflohWorkInstruction { Type = type, Title = "Made-up instruction", Number = "TWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = WorkInstructionStatus.Released, Steps = "[{\"sequence\":10,\"title\":\"Check the seal\"}]", Characteristics = "[]", PartsRows = "[]", Content = "{}" };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }

    [Fact]
    public async Task A_pdf_imported_into_wi_procedures_is_released_and_attached_to_a_work_order()
    {
        var site = await app.CreateSiteAsync();
        var (_, a) = await app.CreateUserAsync([.. Author, Permissions.SequflohReleaseWorkInstruction], site);
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var name = "SOP-" + Guid.NewGuid().ToString("N")[..8];
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");
        var form = new MultipartFormDataContent { { new ByteArrayContent(pdf) { Headers = { ContentType = new("application/pdf") } }, "file", $"{name}.pdf" },
            { new StringContent("Procedure"), "type" }, { new StringContent(site.ToString()), "siteId" } };
        var up = await a.Http.PostAsync("/api/sequfloh/instructions/import", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var imported = await up.JsonAsync();
        var wi = imported.Id();
        Assert.StartsWith(name, imported.Str("number"));
        await a.PostOkAsync($"/api/sequfloh/instructions/{wi}/actions/release", new { effectiveDate = "2026-11-01" });
        var listed = (await a.GetElAsync($"/api/sequfloh/instructions?folder=all&q={name}")).GetProperty("items").EnumerateArray().Select(i => i.Id());
        Assert.Contains(wi, listed);

        var id = (await (await c.PostOkAsync(T, new { type = "Production", siteId = site, workOrder = "WO-PDF-" + name })).JsonAsync()).Id();
        var put = await c.PutAsync($"{T}/{id}/instruction", new { workInstructionId = wi });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        Assert.Equal(wi, (await c.GetElAsync($"{T}/{id}")).GetProperty("record").GetProperty("workInstructionId").GetGuid());
        var (_, v) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateValidationRecord], site);
        var vid = (await (await v.PostOkAsync(T, new { type = "Validation", siteId = site, workOrder = "WO-VAL-" + name })).JsonAsync()).Id();
        put = await v.PutAsync($"{T}/{vid}/instruction", new { workInstructionId = wi });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        Assert.Equal(wi, (await v.GetElAsync($"{T}/{vid}")).GetProperty("record").GetProperty("workInstructionId").GetGuid());
    }

    [Fact]
    public async Task A_work_order_needs_no_instruction_and_one_can_be_attached_and_removed_later()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var id = (await (await c.PostOkAsync(T, new { type = "Production", siteId = site, workOrder = "WO-NOWI" })).JsonAsync()).Id();
        var rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("workInstructionId").ValueKind);
        Assert.Equal(0, rec.GetProperty("steps").GetArrayLength());

        var wi = await ReleasedAsync(site);
        Assert.True((await c.PutAsync($"{T}/{id}/instruction", new { workInstructionId = wi })).IsSuccessStatusCode);
        rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal(wi, rec.GetProperty("workInstructionId").GetGuid());
        Assert.Equal(1, rec.GetProperty("steps").GetArrayLength());

        Assert.True((await c.PutAsync($"{T}/{id}/instruction", new { workInstructionId = (Guid?)null })).IsSuccessStatusCode);
        rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("workInstructionId").ValueKind);
        Assert.Equal(0, rec.GetProperty("steps").GetArrayLength());

        var hist = (await (await app.AdminAsync()).GetElAsync($"{T}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString() ?? "").ToList();
        Assert.Contains(hist, h => h.Contains("work instruction changed from none to"));
        Assert.Contains(hist, h => h.Contains("to none"));

        // started work keeps its instruction as it is
        var ver = rec.GetProperty("stateVersion").GetInt32();
        Assert.True((await c.PostAsync($"{T}/{id}/actions/start", new { operationId = Guid.NewGuid(), stateVersion = ver })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsync($"{T}/{id}/instruction", new { workInstructionId = wi })).StatusCode);
        // a draft instruction can't be attached
        var id2 = (await (await c.PostOkAsync(T, new { type = "Production", siteId = site })).JsonAsync()).Id();
        using (var scope = app.Services.CreateScope()) { var db = await Db(scope); var w = await db.SequflohWorkInstructions.FirstAsync(x => x.Id == wi); w.Status = WorkInstructionStatus.Draft; await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsync($"{T}/{id2}/instruction", new { workInstructionId = wi })).StatusCode);
    }

    [Fact]
    public async Task An_inspection_request_converts_without_an_instruction()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask], site);
        var req = (await (await c.PostOkAsync("/api/sequfloh/inspection-requests", new { siteId = site, partNumber = "MADE-UP-1" })).JsonAsync()).Id();
        var wo = (await (await c.PostOkAsync($"/api/sequfloh/inspection-requests/{req}/convert", new { type = "Production" })).JsonAsync()).GetProperty("workOrderId").GetGuid();
        var rec = (await c.GetElAsync($"{T}/{wo}")).GetProperty("record");
        Assert.Equal(JsonValueKind.Null, rec.GetProperty("workInstructionId").ValueKind);
        using var scope = app.Services.CreateScope();
        Assert.Equal(0, await (await Db(scope)).SequflohWorkInstructions.CountAsync(x => x.SiteId == site)); // nothing was created on the instruction side
    }

    static async Task<JsonElement> Import(ApiClient c, Guid site, string name, string body, string type)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", name);
        form.Add(new StringContent(type), "type");
        form.Add(new StringContent(site.ToString()), "siteId");
        var r = await c.Http.PostAsync("/api/sequfloh/instructions/import", form);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.JsonAsync();
    }

    [Fact]
    public async Task A_procedure_instruction_is_its_file_reimport_revises_it_and_its_type_change_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Author, site);
        var first = await Import(c, site, "Torque procedure.pdf", "%PDF-1.4\n% made-up one\n%%EOF\n", "Procedure");
        var id = first.Id();
        var doc = (await c.GetElAsync($"/api/sequfloh/instructions/{id}")).GetProperty("instruction");
        Assert.Equal("Procedure", doc.Str("type"));
        var schemas = await c.GetElAsync("/api/sequfloh/instructions/schema");
        var proc = schemas.EnumerateArray().First(s => s.Str("type") == "Procedure");
        Assert.Equal(0, proc.GetProperty("sections").GetArrayLength());

        var again = await Import(c, site, "Torque procedure.pdf", "%PDF-1.4\n% made-up two, longer\n%%EOF\n", "Procedure");
        Assert.Equal(id, again.Id());
        Assert.True(again.GetProperty("revised").GetBoolean());
        var files = (await c.GetElAsync($"/api/records/SequflohWorkInstruction/{id}/files?history=false")).EnumerateArray().ToList();
        Assert.Single(files);

        // work orders can't use a procedure instruction
        var list = await c.GetElAsync($"{T}/instructions?type=Production");
        Assert.DoesNotContain(list.EnumerateArray(), x => x.Id() == id);

        Assert.True((await c.PutAsync($"/api/sequfloh/instructions/{id}/type", new { type = "Inspection" })).IsSuccessStatusCode);
        using var scope = app.Services.CreateScope();
        var audit = await (await Db(scope)).AuditEntries.Where(a => a.EntityId == id && a.Action == "Changed instruction type").SingleAsync();
        Assert.Contains("from Procedure Instructions to Inspection", audit.Description);
        Assert.NotNull(audit.UserName);
    }

    [Fact]
    public async Task A_drawing_without_the_family_prefix_never_links_to_a_lookalike_part()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Drawings, site);
        var tag = Random.Shared.Next(100, 999).ToString();
        Guid part;
        using (var scope = app.Services.CreateScope())
        {
            var db = await Db(scope);
            var p = new Part { PartNumber = "MU-9117" + tag, DrawingNumber = "INJ-D1004AA" + tag, Name = "Made-up injector", SiteId = site, Status = MasterStatus.Active };
            db.Parts.Add(p); await db.SaveChangesAsync(); part = p.Id;
        }
        var root = (await (await c.PostOkAsync("/api/drawings/folders", new { name = "Match " + Guid.NewGuid().ToString("N")[..6], siteId = site })).JsonAsync()).Id();
        async Task<Guid> Import(string name)
        {
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% made-up drawing\n%%EOF\n");
            var s = await (await c.PostOkAsync("/api/drawings/import/start", new { folderId = root, fileName = name, size = bytes.LongLength })).JsonAsync();
            await c.Http.PutAsync(s.Str("url"), new ByteArrayContent(bytes));
            return (await (await c.PostOkAsync("/api/drawings/import/finish", new { token = s.Str("token"), revision = "A", linkParts = true, matchNames = Array.Empty<string>() })).JsonAsync()).Id();
        }
        var bare = await Import("D1004AA" + tag + ".pdf");
        var same = await Import("inj_d1004aa" + tag + ".pdf"); // case, dashes and underscores are still ignored
        using var scope2 = app.Services.CreateScope();
        var links = (await Db(scope2)).DrawingPartLinks;
        Assert.False(await links.AnyAsync(l => l.DrawingId == bare && l.PartId == part));
        Assert.True(await links.AnyAsync(l => l.DrawingId == same && l.PartId == part));
    }

    [Fact]
    public async Task Not_applicable_fields_are_kept_per_part_and_new_parts_take_the_family_defaults()
    {
        foreach (var v in new[] { "/", "-", "N/A", " n/a ", "" }) Assert.True(PartFields.IsBlank(v));
        Assert.False(PartFields.IsBlank("Steel"));
        var admin = await app.AdminAsync();
        var site = (await admin.GetElAsync("/api/organization/sites"))[0].Id();
        var fam = (await admin.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(v => v.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync($"/api/sequfloh/reference/values/{fam.Id()}/hidden-fields", new { fields = new[] { "material" } })).StatusCode);
        try
        {
            object Body(string pn, string[]? hidden) => new { part = new { partNumber = pn, name = "Made-up part", partType = 1, revision = "A", unitOfMeasure = "EA", siteId = site, status = 0, hasSpecialCharacteristics = false },
                execution = new { isRepairable = false, isSerialized = false, isLotControlled = false }, typeId = fam.Id(), hiddenFields = hidden };
            var a = (await (await admin.PostOkAsync("/api/sequfloh/parts", Body("NA-" + Guid.NewGuid().ToString("N")[..6], null))).JsonAsync()).Id();
            var pa = (await admin.GetElAsync($"/api/sequfloh/parts/{a}")).GetProperty("part");
            Assert.Equal(["material"], pa.GetProperty("hiddenFields").EnumerateArray().Select(x => x.GetString()).ToArray());
            var b = (await (await admin.PostOkAsync("/api/sequfloh/parts", Body("NA-" + Guid.NewGuid().ToString("N")[..6], ["specification", "drawingNumber"]))).JsonAsync()).Id();
            var pb = (await admin.GetElAsync($"/api/sequfloh/parts/{b}")).GetProperty("part");
            Assert.Equal(["drawingNumber", "specification"], pb.GetProperty("hiddenFields").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/sequfloh/parts", Body("NA-" + Guid.NewGuid().ToString("N")[..6], ["made-up"]))).StatusCode);
            var fields = (await admin.GetElAsync("/api/sequfloh/parts/fields")).EnumerateArray().Select(x => x.Str("label")).ToList();
            Assert.Contains("DWG #", fields);
        }
        finally { await admin.PutAsync($"/api/sequfloh/reference/values/{fam.Id()}/hidden-fields", new { fields = Array.Empty<string>() }); }
    }

    [Fact]
    public async Task A_filled_form_is_listed_under_its_product_family_and_a_family_change_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, editor) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles], site);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,\n")), "file", "made-up fai.csv");
        form.Add(new StringContent(site.ToString()), "siteId");
        form.Add(new StringContent("true"), "blankForm");
        var up = await editor.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var blankId = (await up.JsonAsync()).Id();
        var fam = (await editor.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(v => v.GetProperty("isActive").GetBoolean());
        var famId = fam.GetProperty("id").GetGuid();
        var filled = (await (await editor.PostOkAsync($"/api/imported-files/{blankId}/fill-out", new { title = "FAI line 3", partNumber = "DP-FAI1", familyId = famId })).JsonAsync()).Id();
        var none = (await (await editor.PostOkAsync($"/api/imported-files/{blankId}/fill-out", new { title = "FAI no family" })).JsonAsync()).Id();
        Assert.Equal(famId, (await editor.GetElAsync($"/api/imported-files/{filled}")).GetProperty("familyId").GetGuid());

        bool Listed(JsonElement page, Guid id) => page.GetProperty("items").EnumerateArray().Any(r => r.GetProperty("id").GetGuid() == id);
        var inFam = await editor.GetElAsync($"/api/imported-files?kind=filled&family={famId}&pageSize=200");
        var unassigned = await editor.GetElAsync("/api/imported-files?kind=filled&family=none&pageSize=200");
        Assert.True(Listed(inFam, filled)); Assert.False(Listed(inFam, none));
        Assert.True(Listed(unassigned, none)); Assert.False(Listed(unassigned, filled));

        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PutAsync($"/api/imported-files/{blankId}/family", new { familyId = famId })).StatusCode);
        Assert.True((await editor.PutAsync($"/api/imported-files/{filled}/family", new { familyId = (Guid?)null })).IsSuccessStatusCode);
        Assert.True(Listed(await editor.GetElAsync("/api/imported-files?kind=filled&family=none&pageSize=200"), filled));

        using var scope = app.Services.CreateScope();
        var db = await Db(scope);
        var audit = await db.AuditEntries.Where(a => a.EntityId == filled).ToListAsync();
        Assert.Contains(audit, a => a.Description.Contains("to Unassigned") && a.Description.Contains(fam.GetProperty("label").GetString()!) && a.UserName.Length > 0);
    }
}
