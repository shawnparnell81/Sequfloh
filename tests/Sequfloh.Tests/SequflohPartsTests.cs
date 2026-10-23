using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohPartsTests(TestApp app)
{
    static async Task<System.Text.Json.JsonElement> PostEl(ApiClient c, string url, object body) => await (await c.PostOkAsync(url, body)).JsonAsync();

    static object PartBody(string pn, Guid site, string family = "Pumps") => new
    {
        part = new { partNumber = pn, name = "Pump housing " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null,
            material = "AL6061", hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0 },
        execution = new { productFamily = family, assemblyType = "Sub-assembly", isRepairable = true, isSerialized = true, isLotControlled = false, drawingNumber = "DWG-" + pn, specification = "SPEC-1" }
    };

    [Fact]
    public async Task Part_master_is_shared_and_core_edits_need_master_data_permission()
    {
        var site = await app.CreateSiteAsync();
        var pn = "SQP-" + Guid.NewGuid().ToString("N")[..8];
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], site);
        var (_, sqfOnly) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var created = await PostEl(eng, "/api/sequfloh/parts", PartBody(pn, site));
        var id = created.Id();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Parts.SingleAsync(x => x.Id == id); // the one shared Part Master row
            Assert.Equal("Pumps", p.ProductFamily); Assert.True(p.IsSerialized);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/sequfloh/parts", PartBody(pn + "X", site))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sqfOnly.PostAsync("/api/sequfloh/parts", PartBody(pn + "Y", site))).StatusCode);
        // execution attributes only need sequfloh.edit_parts
        var exec = new { productFamily = "Valves", assemblyType = "Kit", isRepairable = false, isSerialized = false, isLotControlled = true, drawingNumber = "D2", specification = (string?)null };
        Assert.Equal(HttpStatusCode.NoContent, (await sqfOnly.PutAsync($"/api/sequfloh/parts/{id}/execution", exec)).StatusCode);
        var list = await viewer.GetElAsync($"/api/sequfloh/parts?q={pn}&family=Valves");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var detail = await viewer.GetElAsync($"/api/sequfloh/parts/{id}");
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        Assert.NotEmpty((await viewer.GetElAsync($"/api/sequfloh/parts/{id}/history")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await eng.PostAsync($"/api/sequfloh/parts/{id}/deactivate", new { })).StatusCode);
        Assert.Equal(0, (await viewer.GetElAsync($"/api/sequfloh/parts?q={pn}&view=active")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Parts_list_lifecycle_is_numbered_controlled_and_revised_with_snapshots()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.SequflohManagePartsLists], site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var pn = "SQL-" + Guid.NewGuid().ToString("N")[..8];
        var partId = (await PostEl(eng, "/api/sequfloh/parts", PartBody(pn, site))).Id();
        var l = await PostEl(eng, "/api/sequfloh/parts-lists", new { title = "Repair kit " + pn, parentPartId = partId, description = "kit", listType = "RepairKit" });
        var id = l.Id();
        Assert.StartsWith("PL-", l.Str("number"));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/sequfloh/parts-lists", new { title = "x", listType = "Standard" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync($"/api/sequfloh/parts-lists/{id}/items", new { partId = (Guid?)null, partNumber = "", quantity = 0, required = true, traceability = "None" })).StatusCode);
        await PostEl(eng, $"/api/sequfloh/parts-lists/{id}/items", new { partId, quantity = 2, unit = "EA", required = true, traceability = "Serial", notes = "seal" });
        await PostEl(eng, $"/api/sequfloh/parts-lists/{id}/items", new { partNumber = "OR-100", description = "O-ring", quantity = 4, unit = "EA", required = false, traceability = "Lot" });
        var d = await viewer.GetElAsync($"/api/sequfloh/parts-lists/{id}");
        var items = d.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        await eng.PostOkAsync($"/api/sequfloh/parts-lists/{id}/items/reorder", new { ids = new[] { items[1].Id(), items[0].Id() } });
        Assert.Equal("OR-100", (await viewer.GetElAsync($"/api/sequfloh/parts-lists/{id}")).GetProperty("items")[0].Str("partNumber"));
        Assert.Equal(HttpStatusCode.NoContent, (await eng.PostAsync($"/api/sequfloh/parts-lists/{id}/release", new { })).StatusCode);
        // released lists are locked
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PostAsync($"/api/sequfloh/parts-lists/{id}/items", new { partNumber = "X", quantity = 1, required = true, traceability = "None" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await eng.PostAsync($"/api/sequfloh/parts-lists/{id}/revise", new { reason = "Add gasket" })).StatusCode);
        d = await viewer.GetElAsync($"/api/sequfloh/parts-lists/{id}");
        Assert.Equal("Draft", d.GetProperty("list").Str("status"));
        Assert.Equal("B", d.GetProperty("list").Str("revision"));
        var rev = d.GetProperty("revisions")[0];
        Assert.Equal("A", rev.Str("revision")); Assert.Contains("OR-100", rev.Str("snapshot"));
        var copy = await PostEl(eng, $"/api/sequfloh/parts-lists/{id}/duplicate", new { });
        Assert.Equal(2, (await viewer.GetElAsync($"/api/sequfloh/parts-lists/{copy.Id()}")).GetProperty("items").GetArrayLength());
        var hist = await viewer.GetElAsync($"/api/sequfloh/revision-history?q={pn}");
        Assert.DoesNotContain(hist.GetProperty("items").EnumerateArray(), x => x.Str("type") == "Part"); // parts have no revisions to list
        Assert.Equal(1, (await viewer.GetElAsync($"/api/sequfloh/parts/{partId}")).GetProperty("related").GetProperty("partsLists").GetInt32() >= 1 ? 1 : 0);
    }

    [Fact]
    public async Task Operation_steps_are_validated_numbered_duplicated_and_reordered()
    {
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageOperationSteps]);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess]);
        var tag = Guid.NewGuid().ToString("N")[..6];
        object Step(string title, decimal? min = 1, decimal? max = 5) => new { title, category = "Adjustment", detailedAction = "Torque bolts to spec", target = 3m, minimum = min, maximum = max, unit = "Nm", isActive = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/sequfloh/operation-steps", Step("Bad", 9, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/sequfloh/operation-steps", Step("No"))).StatusCode);
        var a = await PostEl(eng, "/api/sequfloh/operation-steps", Step("Torque cover " + tag));
        Assert.StartsWith("OP-", a.Str("number"));
        var b = await PostEl(eng, $"/api/sequfloh/operation-steps/{a.Id()}/duplicate", new { });
        Assert.NotEqual(a.Str("number"), b.Str("number"));
        Assert.Equal(HttpStatusCode.NoContent, (await eng.PostAsync($"/api/sequfloh/operation-steps/{b.Id()}/deactivate", new { })).StatusCode);
        var active = await viewer.GetElAsync($"/api/sequfloh/operation-steps?q={tag}&active=true");
        Assert.Equal(1, active.GetProperty("total").GetInt32());
        var all = (await viewer.GetElAsync("/api/sequfloh/operation-steps?pageSize=200")).GetProperty("items").EnumerateArray().Select(x => x.Id()).ToList();
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PostAsync("/api/sequfloh/operation-steps/reorder", new { ids = all.Take(1) })).StatusCode);
        all.Reverse();
        Assert.Equal(HttpStatusCode.NoContent, (await eng.PostAsync("/api/sequfloh/operation-steps/reorder", new { ids = all })).StatusCode);
    }

    [Fact]
    public async Task Repairable_does_not_put_a_part_on_the_repair_parts_list()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView], site);
        var repairable = (await PostEl(eng, "/api/sequfloh/parts", PartBody("RPB-" + Guid.NewGuid().ToString("N")[..8], site))).Id(); // repairable, not a repair part
        var screw = "RPS-" + Guid.NewGuid().ToString("N")[..8];
        var body = new
        {
            part = new { partNumber = screw, name = "Example screw", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null,
                material = "", hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0 },
            execution = new { productFamily = "Hardware", assemblyType = (string?)null, isRepairable = false, isRepairPart = true, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null }
        };
        var screwId = (await PostEl(eng, "/api/sequfloh/parts", body)).Id();
        var repairParts = (await eng.GetElAsync("/api/sequfloh/parts?view=repair-parts&pageSize=200")).GetProperty("items").EnumerateArray().Select(x => x.Id()).ToList();
        Assert.Contains(screwId, repairParts);
        Assert.DoesNotContain(repairable, repairParts);
        var repairables = (await eng.GetElAsync("/api/sequfloh/parts?view=repairable&pageSize=200")).GetProperty("items").EnumerateArray().Select(x => x.Id()).ToList();
        Assert.Contains(repairable, repairables);
        Assert.DoesNotContain(screwId, repairables);
        var one = (await eng.GetElAsync($"/api/sequfloh/parts/{screwId}")).GetProperty("part");
        Assert.True(one.GetProperty("isRepairPart").GetBoolean());
        Assert.False(one.GetProperty("isRepairable").GetBoolean());
    }
}
