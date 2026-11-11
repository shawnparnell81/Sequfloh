using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Administrator-defined dropdowns stored once and shown on the screens the owner picks. Built-in lists stay separate.</summary>
[Collection("api")]
public sealed class CustomDropdownTests(TestApp app)
{
    const string S = "/api/sequfloh";

    static object Part(string pn, Guid site, Guid typeId) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId, attributes = (object?)null,
    };

    static object Dropdown(string name, object[] values, object[] placements) => new { name, description = "Example finish used on parts.", values, placements };

    static object Value(string label, bool isDefault = false, Guid? id = null, bool isActive = true) => new { id, label, isActive, isDefault };

    static object Place(string screen, bool show = true, bool filter = true, bool csv = true) =>
        new { screen, required = false, showInList = show, includeInCsv = csv, filterable = filter };

    [Fact]
    public async Task Owner_creates_a_dropdown_on_parts_filters_hides_and_confirms_delete()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var name = "Finish " + tag;
        var family = (await admin.GetElAsync($"{S}/reference/product-families")).EnumerateArray().First(x => x.GetProperty("isActive").GetBoolean()).Id();

        var lists = await admin.GetElAsync($"{S}/reference");
        Assert.Contains(lists.EnumerateArray(), x => x.Str("key") == "customers");
        Assert.DoesNotContain(lists.EnumerateArray(), x => x.Str("label") == name);

        var screens = (await admin.GetElAsync($"{S}/custom-dropdowns/screens")).EnumerateArray().Select(x => x.Str("key")).ToList();
        Assert.Contains("parts", screens);
        Assert.Contains("work-orders:Repair", screens);
        Assert.Contains("inspection-requests", screens);
        Assert.Contains("work-instructions", screens);
        Assert.Contains("ncr", screens);
        Assert.Contains("fai-validation", screens);
        Assert.Contains("projects", screens);
        Assert.Contains("chargebacks", screens);
        Assert.Contains("drawings", screens);

        var reader = (await app.CreateUserAsync([Permissions.SequflohAccess])).Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"{S}/custom-dropdowns", Dropdown(name, [Value("Gloss", true), Value("Matte")], [Place("parts")]))).StatusCode);

        var created = await (await admin.PostOkAsync($"{S}/custom-dropdowns", Dropdown(name, [Value("Gloss", true), Value("Matte")], [Place("parts")]))).JsonAsync();
        var id = created.Id();
        var detail = await admin.GetElAsync($"{S}/custom-dropdowns/{id}");
        var gloss = detail.GetProperty("values").EnumerateArray().Single(v => v.Str("label") == "Gloss");
        var matte = detail.GetProperty("values").EnumerateArray().Single(v => v.Str("label") == "Matte");
        Assert.True(gloss.GetProperty("isDefault").GetBoolean());
        Assert.Equal("parts", detail.GetProperty("placements").EnumerateArray().Single().Str("screen"));

        var fields = await admin.GetElAsync($"{S}/custom-fields?screen=parts");
        var field = Assert.Single(fields.EnumerateArray());
        Assert.Equal(name, field.Str("name"));
        Assert.True(field.GetProperty("showInList").GetBoolean());
        Assert.True(field.GetProperty("filterable").GetBoolean());

        var pn = "FIN-" + tag;
        var otherPn = "FIN2-" + tag;
        var partId = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, family))).JsonAsync()).Id();
        var otherId = (await (await admin.PostOkAsync($"{S}/parts", Part(otherPn, site, family))).JsonAsync()).Id();
        await admin.PutOkAsync($"{S}/custom-fields/Part/{partId}", new { label = "part " + pn, values = new[] { new { dropdownId = id, valueId = gloss.Id() } } });

        var row = (await admin.GetElAsync($"{S}/parts?q={pn}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Gloss", row.GetProperty("custom").EnumerateArray().Single().Str("label"));
        var matched = await admin.GetElAsync($"{S}/parts?custom={id}:{gloss.Id()}&q={tag}");
        Assert.Equal(pn, Assert.Single(matched.GetProperty("items").EnumerateArray()).Str("partNumber"));
        var none = await admin.GetElAsync($"{S}/parts?custom={id}:none&q={tag}");
        Assert.Equal(otherPn, Assert.Single(none.GetProperty("items").EnumerateArray()).Str("partNumber"));

        var export = await admin.GetAsync($"{S}/parts/export?q={pn}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains(name, csv);
        Assert.Contains("Gloss", csv);
        var importFields = await admin.GetElAsync($"{S}/parts/import/fields");
        Assert.Contains(importFields.EnumerateArray(), x => x.Str("key") == "custom:" + id && x.Str("label") == name);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Contains($"Created dropdown {name}. Shown on Parts.", await db.AuditEntries.Where(a => a.EntityId == id).Select(a => a.Description).ToListAsync());
            Assert.Contains($"Set {name} on part {pn} to Gloss.", await db.AuditEntries.Where(a => a.EntityId == partId).Select(a => a.Description).ToListAsync());
        }

        var blocked = await admin.PutAsync($"{S}/custom-dropdowns/{id}", Dropdown(name, [new { id = matte.Id(), label = "Matte", isActive = true, isDefault = false }], [Place("parts")]));
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("Deactivate it instead", await blocked.Content.ReadAsStringAsync());

        await admin.PutOkAsync($"{S}/custom-dropdowns/{id}", Dropdown(name,
            [new { id = gloss.Id(), label = "Gloss", isActive = false, isDefault = false }, new { id = matte.Id(), label = "Matte", isActive = true, isDefault = false }],
            []));
        Assert.Empty((await admin.GetElAsync($"{S}/custom-fields?screen=parts")).EnumerateArray());
        Assert.Empty((await admin.GetElAsync($"{S}/custom-fields?screen=parts&recordType=Part&recordId={partId}")).EnumerateArray());
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(gloss.Id(), await db.CustomFieldValues.Where(v => v.RecordId == partId && v.DropdownId == id).Select(v => v.ValueId).SingleAsync());
        }

        await admin.PutOkAsync($"{S}/custom-dropdowns/{id}", Dropdown(name,
            [new { id = gloss.Id(), label = "Gloss", isActive = true, isDefault = true }, new { id = matte.Id(), label = "Matte", isActive = true, isDefault = false }],
            [Place("parts")]));
        var back = Assert.Single((await admin.GetElAsync($"{S}/custom-fields?screen=parts&recordType=Part&recordId={partId}")).EnumerateArray());
        Assert.Equal(gloss.Id(), back.GetProperty("valueId").GetGuid());

        var no = await admin.PostAsync($"{S}/custom-dropdowns/{id}/delete", new { confirm = "" });
        Assert.Equal(HttpStatusCode.BadRequest, no.StatusCode);
        var why = await no.Content.ReadAsStringAsync();
        Assert.Contains(name, why);
        Assert.Contains("1 record", why);
        var wrongCase = await admin.PostAsync($"{S}/custom-dropdowns/{id}/delete", new { confirm = name.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCase.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{S}/custom-dropdowns/{id}/delete", new { confirm = name })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{S}/custom-dropdowns/{id}")).StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.CustomDropdowns.AnyAsync(d => d.Id == id));
            Assert.False(await db.CustomFieldValues.AnyAsync(v => v.DropdownId == id || v.RecordId == otherId && v.DropdownId == id));
            Assert.Contains($"Deleted dropdown {name}. It was cleared from 1 record.", await db.AuditEntries.Where(a => a.EntityId == id && a.Action == "Deleted").Select(a => a.Description).ToListAsync());
        }
        Assert.Contains((await admin.GetElAsync($"{S}/reference")).EnumerateArray(), x => x.Str("key") == "customers");
    }
}
