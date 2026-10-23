using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Part types from the admin-managed list and the cross-reference attributes of a part. Synthetic data only.</summary>
[Collection("api")]
public sealed class PartTypeTests(TestApp app)
{
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static object Body(string pn, Guid site, Guid? typeId, object? attributes = null, string? dwg = null, uint rowVersion = 0) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = dwg, specification = (string?)null },
        typeId, attributes,
    };

    static async Task Put(ApiClient c, string url, object body) { var r = await c.Http.PutAsJsonAsync(url, body); Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); }

    async Task<List<(Guid Id, string Code, string Label, bool Active)>> TypesAsync(ApiClient c) =>
        (await c.GetElAsync("/api/sequfloh/reference/part-types")).EnumerateArray()
            .Select(x => (x.Id(), x.Str("code"), x.Str("label"), x.GetProperty("isActive").GetBoolean())).ToList();

    [Fact]
    public async Task Seeded_types_are_offered_without_a_default_and_admin_can_add_rename_and_retire()
    {
        var admin = await app.AdminAsync();
        var types = await TypesAsync(admin);
        foreach (var name in new[] { "Fuel Pump", "Fuel Injector", "CSA", "Strut", "Brake Wear Sensor", "Air Strut", "Gas Lift", "Electric Lift" })
            Assert.Contains(types, t => t.Code == name.ToUpperInvariant().Replace(' ', '_'));
        var values = await admin.GetElAsync("/api/sequfloh/reference/part-types");
        Assert.DoesNotContain(values.EnumerateArray(), v => v.GetProperty("isDefault").GetBoolean());

        var tag = Guid.NewGuid().ToString("N")[..6];
        var added = await (await admin.PostOkAsync("/api/sequfloh/reference/part-types", new { code = "EX_" + tag.ToUpperInvariant(), label = "Example Type " + tag, isActive = true, isDefault = false })).JsonAsync();
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var id = (await (await eng.PostOkAsync("/api/sequfloh/parts", Body("PT-" + tag, site, added.Id()))).JsonAsync()).Id();

        // a rename shows on the part, because the part stores the type, not its label
        await Put(admin, $"/api/sequfloh/reference/values/{added.Id()}", new { code = "EX_" + tag.ToUpperInvariant(), label = "Renamed Type " + tag, isActive = true, isDefault = false, rowVersion = 0 });
        Assert.Equal("Renamed Type " + tag, (await eng.GetElAsync($"/api/sequfloh/parts/{id}")).GetProperty("part").Str("typeName"));

        // a retired type stays on the part but cannot be chosen for another one
        await admin.PostOkAsync($"/api/sequfloh/reference/values/{added.Id()}/deactivate");
        var r = await eng.Http.PostAsJsonAsync("/api/sequfloh/parts", Body("PT2-" + tag, site, added.Id()));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        uint Rv(JsonElement e) => e.GetProperty("part").GetProperty("rowVersion").GetUInt32();
        await Put(eng, $"/api/sequfloh/parts/{id}", Body("PT-" + tag, site, added.Id(), rowVersion: Rv(await eng.GetElAsync($"/api/sequfloh/parts/{id}"))));
        // once set, the type can be changed but not cleared
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.Http.PutAsJsonAsync($"/api/sequfloh/parts/{id}", Body("PT-" + tag, site, null, rowVersion: Rv(await eng.GetElAsync($"/api/sequfloh/parts/{id}"))))).StatusCode);
    }

    [Fact]
    public async Task Attributes_are_saved_listed_searched_filtered_and_exported()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var strut = (await TypesAsync(eng)).First(t => t.Code == "STRUT").Id;
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var id = (await (await eng.PostOkAsync("/api/sequfloh/parts", Body("OR-" + tag, site, strut,
            new Dictionary<string, string?> { ["nissanVa"] = "NVA-" + tag, ["napaUs"] = "NUS-" + tag, ["napaCa"] = " ", ["keyApplication"] = "Example sedan 2019-2023 front" }, "SN-" + tag))).JsonAsync()).Id();

        var p = (await eng.GetElAsync($"/api/sequfloh/parts/{id}")).GetProperty("part");
        Assert.Equal("Strut", p.Str("typeName"));
        var attrs = p.GetProperty("attributes");
        Assert.Equal("NVA-" + tag, attrs.Str("nissanVa"));
        Assert.False(attrs.TryGetProperty("napaCa", out _)); // blank values are not stored

        // unknown attribute keys are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.Http.PostAsJsonAsync("/api/sequfloh/parts",
            Body("OR2-" + tag, site, strut, new Dictionary<string, string?> { ["madeUp"] = "x" }))).StatusCode);

        var found = await eng.GetElAsync($"/api/sequfloh/parts?q=NUS-{tag}");
        Assert.True(found.GetProperty("total").GetInt32() == 1, found.ToString());
        Assert.Contains("NVA-" + tag, found.GetProperty("items")[0].Str("attributes"));
        Assert.Equal(1, (await eng.GetElAsync($"/api/sequfloh/parts?partType={strut}&q={tag}")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await eng.GetElAsync($"/api/sequfloh/parts?partType=none&q={tag}")).GetProperty("total").GetInt32());

        var csv = await eng.Http.GetStringAsync($"/api/sequfloh/parts/export?q={tag}");
        var lines = csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.StartsWith("OR # (Part number),Name,Description,Revision,Part type,Nissan VA #,NAPA US #,NAPA CA #,Key Application", lines[0]);
        Assert.Contains("SENSEN # (Dwg #)", lines[0]);
        Assert.StartsWith($"OR-{tag},Example part OR-{tag},,A,Strut,NVA-{tag},NUS-{tag},,Example sedan 2019-2023 front", lines[1]);
        Assert.Contains("SN-" + tag, lines[1]);
    }

    [Fact]
    public async Task Import_detects_or_sensen_type_and_cross_reference_columns()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var csv = $"OR#,SENSEN #,Description,Part Type,Nissan VA,NAPA US,NAPA CA,Key Application\n" +
                  $"I-{tag}-1,SN-{tag}-1,Example strut,Strut,NV1,NU1,NC1,Example coupe rear\n" +
                  $"I-{tag}-2,SN-{tag}-2,Example pump,Fuel pump,NV2,,,\n" +
                  $"I-{tag}-3,,Example thing,Not A Type,,,,\n" +
                  $"I-{tag}-4,,Example untyped,,,,,\n";
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv)); file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "parts.csv");
        var t = await (await eng.Http.PostAsync("/api/sequfloh/parts/import/read", form)).JsonAsync();
        var map = t.GetProperty("mapping");
        Assert.Equal(0, map.GetProperty("partNumber").GetInt32());
        Assert.Equal(1, map.GetProperty("drawingNumber").GetInt32());
        Assert.Equal(2, map.GetProperty("description").GetInt32());
        Assert.Equal(3, map.GetProperty("typeName").GetInt32());
        Assert.Equal(4, map.GetProperty("attr:nissanVa").GetInt32());
        Assert.Equal(5, map.GetProperty("attr:napaUs").GetInt32());
        Assert.Equal(6, map.GetProperty("attr:napaCa").GetInt32());
        Assert.Equal(7, map.GetProperty("attr:keyApplication").GetInt32());
        // a "Dwg #" heading is the drawing number too
        Assert.Equal(1, Sequfloh.Infrastructure.Modules.Execution.SequflohPartsImport.Suggest(["Part number", "Dwg #"])["drawingNumber"]);

        var req = new { fileName = "parts.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = map, siteId = site, updateExisting = false };
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import", req)).JsonAsync();
        Assert.Equal(3, plan.GetProperty("add").GetInt32());
        Assert.Equal(1, plan.GetProperty("errors").GetInt32());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith($"I-{tag}-")).OrderBy(p => p.PartNumber).ToListAsync();
        var types = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == "part-types").ToDictionaryAsync(v => v.Id, v => v.Label);
        Assert.Equal("Strut", types[parts[0].TypeId!.Value]);
        Assert.Equal($"SN-{tag}-1", parts[0].DrawingNumber);
        Assert.Equal("Example coupe rear", JsonDocument.Parse(parts[0].Attributes!).RootElement.Str("keyApplication"));
        Assert.Equal("Fuel Pump", types[parts[1].TypeId!.Value]);
        Assert.Null(parts[2].TypeId); // untyped rows are imported and can be set later
    }
}
