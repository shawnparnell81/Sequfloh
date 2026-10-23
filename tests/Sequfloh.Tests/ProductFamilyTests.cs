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

/// <summary>Product families from the admin-managed list and the cross-reference attributes of a part. Synthetic data only.</summary>
[Collection("api")]
public sealed class ProductFamilyTests(TestApp app)
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
        (await c.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray()
            .Select(x => (x.Id(), x.Str("code"), x.Str("label"), x.GetProperty("isActive").GetBoolean())).ToList();

    [Fact]
    public async Task Seeded_families_are_offered_without_a_default_and_admin_can_add_rename_and_retire()
    {
        var admin = await app.AdminAsync();
        var types = await TypesAsync(admin);
        foreach (var name in new[] { "Complete Strut Assembly (CSA)", "Fuel Pump", "Fuel Injector", "Brake Wear Sensor", "Gas Lift", "Electric Lift", "Coil Spring", "Struts", "Air Struts" })
            Assert.Contains(types, t => t.Label == name);
        var values = await admin.GetElAsync("/api/sequfloh/reference/product-families");
        Assert.DoesNotContain(values.EnumerateArray(), v => v.GetProperty("isDefault").GetBoolean());

        var tag = Guid.NewGuid().ToString("N")[..6];
        var added = await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "EX_" + tag.ToUpperInvariant(), label = "Example Type " + tag, isActive = true, isDefault = false })).JsonAsync();
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
        var strut = (await TypesAsync(eng)).First(t => t.Code == "STRUTS").Id;
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var id = (await (await eng.PostOkAsync("/api/sequfloh/parts", Body("OR-" + tag, site, strut,
            new Dictionary<string, string?> { ["nissanVa"] = "NVA-" + tag, ["napaUs"] = "NUS-" + tag, ["napaCa"] = " ", ["keyApplication"] = "Example sedan 2019-2023 front" }, "SN-" + tag))).JsonAsync()).Id();

        var p = (await eng.GetElAsync($"/api/sequfloh/parts/{id}")).GetProperty("part");
        Assert.Equal("Struts", p.Str("typeName"));
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
        Assert.StartsWith("OR # (Part number),Name,Description,Revision,Product family,Nissan VA #,NAPA US #,NAPA CA #,Key Application,Position", lines[0]);
        Assert.Contains("SENSEN # (Dwg #)", lines[0]);
        Assert.StartsWith($"OR-{tag},Example part OR-{tag},,A,Struts,NVA-{tag},NUS-{tag},,Example sedan 2019-2023 front", lines[1]);
        Assert.Contains("SN-" + tag, lines[1]);
    }

    [Fact]
    public async Task Import_detects_or_sensen_type_and_cross_reference_columns()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var csv = $"OR#,SENSEN #,Description,Product Family,Nissan VA,NAPA US,NAPA CA,Key Application\n" +
                  $"I-{tag}-1,SN-{tag}-1,Example strut,Struts,NV1,NU1,NC1,Example coupe rear\n" +
                  $"I-{tag}-2,SN-{tag}-2,Example pump,Fuel pump,NV2,,,\n" +
                  $"I-{tag}-3,,Example thing,Not A Type,,,,\n" +
                  $"I-{tag}-4,,Example untyped,,,,,\n" +
                  $"I-{tag}-5,,Example assembly,Complete Strut Assembly,,,,\n" +
                  $"I-{tag}-6,,Example assembly two,csa,,,,\n";
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

        var req = new { fileName = "parts.csv", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), mapping = map, siteId = site, updateExisting = false, @fixed = new Dictionary<string, string> { ["name"] = "Struts" } };
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import", req)).JsonAsync();
        Assert.Equal(5, plan.GetProperty("add").GetInt32());
        Assert.Equal(1, plan.GetProperty("errors").GetInt32());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith($"I-{tag}-")).OrderBy(p => p.PartNumber).ToListAsync();
        var types = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == "product-families").ToDictionaryAsync(v => v.Id, v => v.Label);
        Assert.Equal("Struts", types[parts[0].TypeId!.Value]);
        Assert.Equal($"SN-{tag}-1", parts[0].DrawingNumber);
        Assert.Equal("Example coupe rear", JsonDocument.Parse(parts[0].Attributes!).RootElement.Str("keyApplication"));
        Assert.Equal("Fuel Pump", types[parts[1].TypeId!.Value]);
        Assert.Null(parts[2].TypeId); // rows without a family are imported and can be set later
        Assert.Equal("Complete Strut Assembly (CSA)", types[parts[3].TypeId!.Value]);
        Assert.Equal("Complete Strut Assembly (CSA)", parts[4].ProductFamily);
    }

    [Fact]
    public async Task Cross_reference_sheet_with_a_title_row_imports_values_as_shown_with_defaults_for_all_rows()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        byte[] bytes;
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("Cross Data");
            ws.Cell(1, 2).Value = "No Drawing"; // a stray title above the headings
            string[] head = ["SENSEN#", "OR#", "Nissan VA #", "NAPA US #", "NAPA CA #", "Key Application", "POSITION"];
            for (var c = 0; c < head.Length; c++) ws.Cell(2, c + 1).Value = head[c];
            object?[][] rows =
            [
                ["9000-0001", $"X{tag}1", "VA-1", "LS1", "9001X", "2000-2006 Example Sedan", "Rear"],
                ["9000-0002", $"X{tag}2", null, 281427, "  9002X ", "1996-2000 Example Coupe", "Front Left"],
                ["9000-0003", null, null, null, null, null, null],               // drawing number only: no OR #
                ["9000-0004", $"x{tag}1", null, null, null, "Example duplicate", null], // same OR # as the first row, other case
                [null, null, null, null, null, null, null],                       // blank row
                ["9000-0005", $"X{tag}3", null, null, null, null, null],          // nothing for a name
            ];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < head.Length; c++)
                    switch (rows[r][c]) { case string v: ws.Cell(r + 3, c + 1).Value = v; break; case int n: ws.Cell(r + 3, c + 1).Value = n; break; }
            ws.Cell(3, 5).Value = 123; ws.Cell(3, 5).Style.NumberFormat.Format = "00000"; // shown with leading zeros
            using var ms = new MemoryStream(); wb.SaveAs(ms); bytes = ms.ToArray();
        }
        async Task<JsonElement> Read(int? headerRow = null)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "cross.xlsx");
            var r = await eng.Http.PostAsync("/api/sequfloh/parts/import/read" + (headerRow is { } h ? $"?headerRow={h}" : ""), form);
            var b = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, b);
            return JsonDocument.Parse(b).RootElement;
        }
        var t = await Read();
        Assert.Equal(2, t.GetProperty("headerRow").GetInt32());
        Assert.Equal("SENSEN#", t.GetProperty("headers")[0].GetString());
        Assert.Equal([3, 4, 5, 6, 8], t.GetProperty("rowNumbers").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(1, t.GetProperty("top")[0].GetProperty("row").GetInt32());
        var map = t.GetProperty("mapping");
        Assert.Equal(0, map.GetProperty("drawingNumber").GetInt32());
        Assert.Equal(1, map.GetProperty("partNumber").GetInt32());
        Assert.Equal(6, map.GetProperty("attr:position").GetInt32());
        Assert.False(map.TryGetProperty("name", out _));
        // the header row can be chosen; row 1 gives one column heading only
        Assert.Equal("No Drawing", (await Read(1)).GetProperty("headers")[1].GetString());

        var csa = (await TypesAsync(eng)).Single(x => x.Label == "Complete Strut Assembly (CSA)").Id;
        var req = new { fileName = "cross.xlsx", headers = t.GetProperty("headers"), rows = t.GetProperty("rows"), rowNumbers = t.GetProperty("rowNumbers"), mapping = map,
            siteId = site, updateExisting = false, defaultTypeId = csa, @fixed = new Dictionary<string, string> { ["name"] = "CSA" } };
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import", req)).JsonAsync();
        Assert.Equal(3, plan.GetProperty("add").GetInt32());
        Assert.Equal(2, plan.GetProperty("skip").GetInt32());
        Assert.Equal(0, plan.GetProperty("errors").GetInt32());
        var byRow = plan.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("row").GetInt32());
        Assert.Equal("Skip", byRow[5].Str("action"));
        Assert.Contains("No OR # (Part number)", byRow[5].GetProperty("messages")[0].GetString());
        Assert.Equal("Skip", byRow[6].Str("action"));
        Assert.Contains("Same OR # (Part number) as row 3", byRow[6].GetProperty("messages")[0].GetString());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith("X" + tag)).ToDictionaryAsync(p => p.PartNumber);
        var a = parts[$"X{tag}1"]; var b = parts[$"X{tag}2"]; var c3 = parts[$"X{tag}3"];
        Assert.All(parts.Values, p => { Assert.Equal(csa, p.TypeId); Assert.Equal("Complete Strut Assembly (CSA)", p.ProductFamily); });
        Assert.Equal("9000-0001", a.DrawingNumber);
        Assert.Equal("Complete Strut Assembly (CSA)", a.Name);
        Assert.Equal("Complete Strut Assembly (CSA)", c3.Name);
        var av = JsonDocument.Parse(a.Attributes!).RootElement; var bv = JsonDocument.Parse(b.Attributes!).RootElement;
        Assert.Equal("00123", av.Str("napaCa"));
        Assert.Equal("2000-2006 Example Sedan", av.Str("keyApplication"));
        Assert.Equal("Rear", av.Str("position"));
        Assert.Equal("281427", bv.Str("napaUs"));
        Assert.Equal("9002X", bv.Str("napaCa"));
        Assert.False(bv.TryGetProperty("nissanVa", out _));
    }

    [Fact]
    public async Task Typed_product_family_text_is_linked_to_the_list_and_renames_follow()
    {
        var site = await app.CreateSiteAsync();
        var (_, admin) = await app.CreateUserAsync([.. Editor, Permissions.SequflohManageDropdowns], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Parts.Add(new Sequfloh.Domain.MasterData.Part { PartNumber = $"L-{tag}-1", Name = "Example", Revision = "A", UnitOfMeasure = "EA", SiteId = site, ProductFamily = "CSA" });
        db.Parts.Add(new Sequfloh.Domain.MasterData.Part { PartNumber = $"L-{tag}-2", Name = "Example", Revision = "A", UnitOfMeasure = "EA", SiteId = site, ProductFamily = "Example family " + tag });
        await db.SaveChangesAsync();
        await Sequfloh.Infrastructure.Modules.Execution.SequflohFoundationService.LinkProductFamiliesAsync(db, CancellationToken.None);
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith($"L-{tag}-")).OrderBy(p => p.PartNumber).ToListAsync();
        var values = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == "product-families").ToListAsync();
        Assert.Equal("Complete Strut Assembly (CSA)", values.Single(v => v.Id == parts[0].TypeId).Label);
        Assert.Equal("Complete Strut Assembly (CSA)", parts[0].ProductFamily);
        var added = values.Single(v => v.Id == parts[1].TypeId);
        Assert.Equal("Example family " + tag, added.Label);

        await Put(admin, $"/api/sequfloh/reference/values/{added.Id}", new { code = added.Code, label = "Renamed family " + tag, isActive = true, isDefault = false });
        Assert.Equal("Renamed family " + tag, await db.Parts.AsNoTracking().Where(p => p.Id == parts[1].Id).Select(p => p.ProductFamily).SingleAsync());
    }

    [Fact]
    public async Task Choice_fields_take_one_value_for_all_rows_or_a_matching_column_value()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fields = (await eng.GetElAsync("/api/sequfloh/parts/import/fields")).EnumerateArray().ToDictionary(x => x.Str("key")!, x => x.GetProperty("choices"));
        Assert.Equal("families", fields["name"].GetString());
        Assert.Equal("families", fields["partClass"].GetString());
        Assert.Equal("units", fields["unitOfMeasure"].GetString());
        Assert.Equal("yesNo", fields["isAssembly"].GetString());
        Assert.Equal("lot", fields["isLotControlled"].GetString());
        Assert.Equal(JsonValueKind.Null, fields["material"].ValueKind);

        string[] headers = ["OR#", "UOM", "Lot", "Repairable", "Class"];
        string[][] rows =
        [
            [$"C{tag}1", "ea", "n/a", "yes", "fuel pump"],
            [$"C{tag}2", "SKID", "No", "No", "Struts"],
            [$"C{tag}3", "Pallet", "Maybe", "No", "Widget"], // three values that are not choices
        ];
        var req = new { fileName = "choices.csv", headers, rows, siteId = site, updateExisting = false,
            mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["unitOfMeasure"] = 1, ["isLotControlled"] = 2, ["isRepairable"] = 3, ["partClass"] = 4 },
            @fixed = new Dictionary<string, string> { ["typeName"] = "CSA", ["name"] = "Coil Spring", ["isAssembly"] = "Yes" } };
        var plan = await (await eng.PostOkAsync("/api/sequfloh/parts/import", req)).JsonAsync();
        Assert.Equal(2, plan.GetProperty("add").GetInt32());
        Assert.Equal(1, plan.GetProperty("errors").GetInt32());
        var bad = plan.GetProperty("rows").EnumerateArray().Single(x => x.Str("action") == "Error");
        var msgs = string.Join(" ", bad.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Unit of measure \"Pallet\"", msgs);
        Assert.Contains("Lot controlled \"Maybe\"", msgs);
        Assert.Contains("Part class \"Widget\"", msgs);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith("C" + tag)).OrderBy(p => p.PartNumber).ToListAsync();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => { Assert.Equal("Coil Spring", p.Name); Assert.Equal("Complete Strut Assembly (CSA)", p.ProductFamily); Assert.True(p.IsAssembly); Assert.False(p.IsRepairPart); });
        Assert.Equal(("Each", "Not applicable", false, true, "Fuel Pump"), (parts[0].UnitOfMeasure, parts[0].LotControl, parts[0].IsLotControlled, parts[0].IsRepairable, parts[0].PartClass));
        Assert.Equal(("Skid", "No", false, "Struts"), (parts[1].UnitOfMeasure, parts[1].LotControl, parts[1].IsRepairable, parts[1].PartClass));

        // a text field cannot be the same for all rows
        var r = await eng.Http.PostAsJsonAsync("/api/sequfloh/parts/import/preview", new { fileName = "x.csv", headers, rows, siteId = site, updateExisting = false,
            mapping = new Dictionary<string, int> { ["partNumber"] = 0 }, @fixed = new Dictionary<string, string> { ["material"] = "Steel" } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);

        // Name must be chosen for all rows and never comes from a file column
        var noName = await eng.Http.PostAsJsonAsync("/api/sequfloh/parts/import/preview", new { fileName = "x.csv", headers, rows, siteId = site, updateExisting = false,
            mapping = new Dictionary<string, int> { ["partNumber"] = 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("Choose the Name for all rows", await noName.Content.ReadAsStringAsync());
        var fromFile = await eng.Http.PostAsJsonAsync("/api/sequfloh/parts/import/preview", new { fileName = "x.csv", headers, rows, siteId = site, updateExisting = false,
            mapping = new Dictionary<string, int> { ["partNumber"] = 0, ["name"] = 4 }, @fixed = new Dictionary<string, string> { ["name"] = "Struts" } });
        Assert.Equal(HttpStatusCode.BadRequest, fromFile.StatusCode);
    }

    [Fact]
    public async Task Part_form_saves_part_class_assembly_and_lot_control()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Editor, site);
        var pn = "F-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fuel = (await TypesAsync(eng)).Single(x => x.Label == "Fuel Pump").Id;
        object Make(string? partClass, string lot) => new
        {
            part = new { partNumber = pn, name = "Fuel Pump", description = "", partType = 0, revision = "A", unitOfMeasure = "Batch", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { isRepairable = true, isSerialized = false, isLotControlled = false, partClass, isAssembly = true, lotControl = lot },
            typeId = fuel,
        };
        var bad = await eng.Http.PostAsJsonAsync("/api/sequfloh/parts", Make("Not a family", "Yes"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var id = (await (await eng.PostOkAsync("/api/sequfloh/parts", Make("Gas Lift", "Yes"))).JsonAsync()).GetProperty("id").GetGuid();
        var p = (await eng.GetElAsync($"/api/sequfloh/parts/{id}")).GetProperty("part");
        Assert.Equal("Gas Lift", p.Str("partClass"));
        Assert.True(p.GetProperty("isAssembly").GetBoolean());
        Assert.Equal("Yes", p.Str("lotControl"));
        Assert.True(p.GetProperty("isLotControlled").GetBoolean());
        Assert.False(p.GetProperty("isRepairPart").GetBoolean());
        Assert.Equal(1, (await eng.GetElAsync($"/api/sequfloh/parts?view=assemblies&q={pn}")).GetProperty("total").GetInt32());
    }
}
