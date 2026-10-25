using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohInstructionTests(TestApp app)
{
    static async Task<JsonElement> PostEl(ApiClient c, string url, object body) => await (await c.PostOkAsync(url, body)).JsonAsync();
    static async Task PutOk(ApiClient c, string url, object body)
    {
        var r = await c.PutAsync(url, body);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"PUT {url} failed: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }
    static readonly string[] AuthorPerms = [Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohEditDraftWorkInstruction];
    static readonly string[] ReleaserPerms = [Permissions.SequflohAccess, Permissions.SequflohReleaseWorkInstruction, Permissions.SequflohReviseWorkInstruction, Permissions.SequflohObsoleteWorkInstruction,
        Permissions.SequflohEditDraftWorkInstruction];

    static object Save(JsonElement wi, Guid site, object? content = null, object[]? steps = null, object[]? chars = null, object[]? parts = null, string? effective = null, Guid? partId = null,
        string? repairDescription = null, uint rowVersion = 0) => new
    {
        title = wi.GetProperty("instruction").Str("title"), siteId = site, departmentId = (Guid?)null, ownerId = (Guid?)null,
        partId = partId ?? (wi.GetProperty("instruction").GetProperty("partId").ValueKind == JsonValueKind.Null ? (Guid?)null : wi.GetProperty("instruction").GetProperty("partId").GetGuid()),
        repairDescription, effectiveDate = effective, content = content ?? new { }, steps = steps ?? [], characteristics = chars ?? [], partsRows = parts ?? [], rowVersion
    };

    [Fact]
    public async Task Seven_instruction_types_open_their_own_designer_and_numbering()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync(AuthorPerms, site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var schema = (await author.GetElAsync("/api/sequfloh/instructions/schema")).EnumerateArray().ToList();
        Assert.Equal(7, schema.Count);
        Assert.Contains(schema, s => s.Str("type") == "Inspection" && s.Str("title") == "Inspection");
        Assert.Contains(schema, s => s.Str("type") == "RepairWork" && s.GetProperty("hasPartsRows").GetBoolean());
        Assert.Contains(schema, s => s.Str("type") == "Fai" && s.GetProperty("hasCharacteristics").GetBoolean());
        var prefixes = new Dictionary<string, string> { ["Validation"] = "VWI-", ["ValidationFai"] = "WI-", ["Fai"] = "FWI-", ["RepairTest"] = "RTI-", ["RepairWork"] = "RWI-", ["Inspection"] = "IWI-" };
        foreach (var (type, prefix) in prefixes)
        {
            var c = await PostEl(author, "/api/sequfloh/instructions", new { type, title = $"{type} instruction", siteId = site });
            Assert.StartsWith("T-", c.Str("number")); // never a generated prefix
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/sequfloh/instructions", new { type = "Fai", title = "x", siteId = site })).StatusCode);
        var repairFolder = await viewer.GetElAsync("/api/sequfloh/instructions?folder=repair&pageSize=100");
        Assert.All(repairFolder.GetProperty("items").EnumerateArray(), i => Assert.StartsWith("Repair", i.Str("type")));
    }

    [Fact]
    public async Task Draft_is_released_with_one_button_and_released_records_change_only_by_revision()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync(AuthorPerms, site);
        var (_, releaser) = await app.CreateUserAsync(ReleaserPerms, site);
        var id = (await PostEl(author, "/api/sequfloh/instructions", new { type = "Validation", title = "Seal leak validation", siteId = site })).Id();
        var url = $"/api/sequfloh/instructions/{id}";
        var wi = await author.GetElAsync(url);
        Assert.Equal("Draft", wi.GetProperty("instruction").Str("status"));
        // the old review steps are gone
        foreach (var old in new[] { "submit-review", "return", "submit-approval", "approve", "archive", "restore" })
            Assert.Equal(HttpStatusCode.NotFound, (await releaser.PostAsync($"{url}/actions/{old}", new { })).StatusCode);
        // limits are validated, sequence must ascend
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(url, Save(wi, site, steps: [new { sequence = 10, title = "Pressurize", lowerLimit = 9, upperLimit = 2 }]))).StatusCode);
        var content = new { validationAcceptance = new { overallAcceptanceCriteria = "No leak at 150 psi for 10 minutes" }, purpose = new { purpose = "Prove seal integrity", bogus = "dropped" } };
        await PutOk(author, url, Save(wi, site, content, [new { sequence = 20, title = "Leak check" }, new { sequence = 10, title = "Pressurize", target = 150, lowerLimit = 145, upperLimit = 155, unit = "psi" }]));
        Assert.Equal(HttpStatusCode.BadRequest, (await releaser.PostAsync($"{url}/actions/release", new { effectiveDate = "2026-11-01" })).StatusCode); // out of order
        wi = await author.GetElAsync(url);
        Assert.False(wi.GetProperty("instruction").GetProperty("content").GetProperty("purpose").TryGetProperty("bogus", out _));
        await PutOk(author, url, Save(wi, site, content, [new { sequence = 10, title = "Pressurize", target = 150, lowerLimit = 145, upperLimit = 155, unit = "psi" }, new { sequence = 20, title = "Leak check" }]));
        // only someone whose role allows it can release
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsync($"{url}/actions/release", new { effectiveDate = "2026-11-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await releaser.PostAsync($"{url}/actions/release", new { })).StatusCode); // effective date required
        await releaser.PostOkAsync($"{url}/actions/release", new { effectiveDate = "2026-11-01" });
        Assert.Equal(HttpStatusCode.Conflict, (await releaser.PostAsync($"{url}/actions/release", new { effectiveDate = "2026-11-01" })).StatusCode); // already released
        wi = await author.GetElAsync(url);
        Assert.Equal("Released", wi.GetProperty("instruction").Str("status"));
        Assert.Equal(HttpStatusCode.Conflict, (await author.PutAsync(url, Save(wi, site, content))).StatusCode); // released is read-only
        Assert.Equal(["create", "release"], wi.GetProperty("transitions").EnumerateArray().Select(t => t.Str("action")).ToList());

        Assert.Equal(HttpStatusCode.BadRequest, (await releaser.PostAsync($"{url}/revise", new { reason = "" })).StatusCode);
        var rev = await PostEl(releaser, $"{url}/revise", new { reason = "Raise test pressure" });
        Assert.Equal("B", rev.Str("revision"));
        Assert.Equal(HttpStatusCode.Conflict, (await releaser.PostAsync($"{url}/revise", new { reason = "again" })).StatusCode);
        var bUrl = $"/api/sequfloh/instructions/{rev.Id()}";
        var b = await releaser.GetElAsync(bUrl);
        Assert.Equal("Draft", b.GetProperty("instruction").Str("status"));
        Assert.Equal(2, b.GetProperty("instruction").GetProperty("steps").GetArrayLength());
        await releaser.PostOkAsync($"{bUrl}/actions/release", new { effectiveDate = "2026-12-01" }); // the same person may release their own revision
        Assert.Equal("Superseded", (await author.GetElAsync(url)).GetProperty("instruction").Str("status"));
        Assert.Equal(2, (await author.GetElAsync(bUrl)).GetProperty("revisions").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await releaser.PostAsync($"{bUrl}/actions/obsolete", new { })).StatusCode);
        await releaser.PostOkAsync($"{bUrl}/actions/obsolete", new { comment = "Product discontinued" });
        Assert.Equal("Obsolete", (await author.GetElAsync(bUrl)).GetProperty("instruction").Str("status"));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var trail = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == id || a.EntityId == rev.Id()).ToListAsync();
        var release = trail.First(a => a.EntityId == id && a.Action == "WorkInstructionRelease");
        Assert.False(string.IsNullOrWhiteSpace(release.UserName));
        Assert.Contains("released", release.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trail, a => a.EntityId == rev.Id() && a.Action == "WorkInstructionObsolete" && a.Reason == "Product discontinued");
    }

    [Fact]
    public async Task Repair_instruction_copies_library_data_without_linking_back_and_fai_characteristics_are_controlled()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync([.. AuthorPerms, Permissions.SequflohManageOperationSteps, Permissions.SequflohManagePartsLists, Permissions.SequflohEditParts, Permissions.MasterDataEdit], site);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var partId = (await PostEl(author, "/api/sequfloh/parts", new
        {
            part = new { partNumber = "RW-" + tag, name = "Valve body", partType = 0, revision = "A", unitOfMeasure = "EA", siteId = site, status = 0, hasSpecialCharacteristics = false },
            execution = new { productFamily = "Valves", isRepairable = true, isSerialized = true, isLotControlled = false }
        })).Id();
        var op = await PostEl(author, "/api/sequfloh/operation-steps", new { title = "Replace seal " + tag, category = "Assembly", detailedAction = "Remove and replace the shaft seal", minimum = 10, maximum = 12, unit = "Nm" });
        var list = (await PostEl(author, "/api/sequfloh/parts-lists", new { title = "Seal kit " + tag, listType = "RepairKit" })).Id();
        await author.PostOkAsync($"/api/sequfloh/parts-lists/{list}/items", new { partNumber = "SEAL-" + tag, description = "Shaft seal", quantity = 2, unit = "EA", required = true, traceability = "Lot" });

        var id = (await PostEl(author, "/api/sequfloh/instructions", new { type = "RepairWork", title = "Valve seal repair", siteId = site, partId })).Id();
        var url = $"/api/sequfloh/instructions/{id}";
        Assert.Equal(1, (await PostEl(author, $"{url}/copy-operation-steps", new { operationStepIds = new[] { op.Id() } })).GetProperty("copied").GetInt32());
        Assert.Equal(1, (await PostEl(author, $"{url}/copy-parts", new { partsListId = list })).GetProperty("copied").GetInt32());
        var wi = await author.GetElAsync(url);
        var step = wi.GetProperty("instruction").GetProperty("steps")[0];
        Assert.Equal("Remove and replace the shaft seal", step.Str("detailedRepairAction"));
        Assert.True(wi.GetProperty("instruction").GetProperty("partsRows")[0].GetProperty("lotNumberRequired").GetBoolean());
        Assert.Contains("Choose the repair description.", wi.GetProperty("releaseProblems").EnumerateArray().Select(x => x.GetString()));
        // edit the copy: the library template does not change
        var edited = new { id = step.Str("id"), sequence = 10, title = "Replace seal (edited)", detailedRepairAction = "Changed", minimum = 10, maximum = 12 };
        await PutOk(author, url, Save(wi, site, steps: [edited], parts: [new { replacementPartNumber = "SEAL-" + tag, quantity = 2, required = true }], repairDescription: "Component replacement"));
        Assert.Equal("Remove and replace the shaft seal", (await author.GetElAsync($"/api/sequfloh/operation-steps/{op.Id()}")).Str("detailedAction"));
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(url, Save(wi, site, repairDescription: "Not a seeded description"))).StatusCode);
        Assert.Equal(["Enter the effective date before release."], (await author.GetElAsync(url)).GetProperty("releaseProblems").EnumerateArray().Select(p => p.GetString()).ToList());

        // FAI characteristics
        var fai = (await PostEl(author, "/api/sequfloh/instructions", new { type = "Fai", title = "Housing FAI", siteId = site })).Id();
        var furl = $"/api/sequfloh/instructions/{fai}";
        var f = await author.GetElAsync(furl);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PutAsync(furl, Save(f, site, chars: [new { characteristicNumber = "1", requirement = "Bore", lowerLimit = 10.02, upperLimit = 10.0 }]))).StatusCode);
        await PutOk(author, furl, Save(f, site, chars: [new { characteristicNumber = "1", requirement = "Bore diameter", lowerLimit = 9.98, upperLimit = 10.02, unit = "mm" }, new { characteristicNumber = "1", requirement = "Dup" }]));
        Assert.Contains("Characteristic numbers must be unique.", (await author.GetElAsync(furl)).GetProperty("releaseProblems").EnumerateArray().Select(x => x.GetString()));
        var fai2 = (await PostEl(author, "/api/sequfloh/instructions", new { type = "Fai", title = "Housing FAI 2", siteId = site })).Id();
        Assert.Equal(2, (await PostEl(author, $"/api/sequfloh/instructions/{fai2}/copy-characteristics", new { fromInstructionId = fai })).GetProperty("copied").GetInt32());
        // stale save is refused
        var cur = (await author.GetElAsync(furl)).GetProperty("instruction").GetProperty("rowVersion").GetUInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await author.PutAsync(furl, Save(f, site, rowVersion: cur + 999))).StatusCode);
    }

    [Fact]
    public async Task Each_instruction_type_prints_a_controlled_pdf_and_printing_is_audited()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync(AuthorPerms, site);
        var (_, outsider) = await app.CreateUserAsync([Permissions.SequflohAccess], await app.CreateSiteAsync());
        foreach (var type in new[] { "Validation", "ValidationFai", "Fai", "RepairTest", "RepairWork", "Inspection" })
        {
            var c = await PostEl(author, "/api/sequfloh/instructions", new { type, title = $"Print {type}", siteId = site });
            var url = $"/api/sequfloh/instructions/{c.Str("id")}";
            var wi = await author.GetElAsync(url);
            await PutOk(author, url, Save(wi, site, content: new { purpose = new { purpose = "Line one\nLine two" } }, steps: [new { sequence = 10, title = "First step" }],
                chars: type is "Fai" or "ValidationFai" ? [new { sequence = 10, characteristicNumber = "1", requirement = "Length 10 mm" }] : null,
                parts: type == "RepairWork" ? [new { sequence = 10, replacementPartNumber = "P-1", quantity = 2 }] : null));
            var r = await author.GetAsync($"{url}/print");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("application/pdf", r.Content.Headers.ContentType?.MediaType);
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{url}/print")).StatusCode);
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == Guid.Parse(c.Str("id")) && a.Action == "WorkInstructionPrinted"));
        }
    }
}
