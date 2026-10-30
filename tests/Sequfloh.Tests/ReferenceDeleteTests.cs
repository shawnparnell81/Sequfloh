using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Deleting dropdown values for good. Made-up data only.</summary>
[Collection("api")]
public sealed class ReferenceDeleteTests(TestApp app)
{
    const string S = "/api/sequfloh";

    static object Part(string pn, Guid site, Guid? typeId) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
        typeId, attributes = (object?)null,
    };

    static async Task<bool> HasAsync(ApiClient c, string key, Guid id) =>
        (await c.GetElAsync($"{S}/reference/{key}")).EnumerateArray().Any(v => v.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Unused_value_is_deleted_and_audited()
    {
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var id = (await (await admin.PostOkAsync($"{S}/reference/inspection-reasons", new { code = "", label = "Old reason " + tag, description = (string?)null })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/reference/values/{id}")).StatusCode);
        Assert.False(await HasAsync(admin, "inspection-reasons", id));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == id && a.Action == "ReferenceValueDeleted" && a.Description == $"Deleted \"Old reason {tag}\" from Inspection request reasons"));
    }

    [Fact]
    public async Task Family_used_by_parts_cannot_be_deleted()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code = $"D{tag}", label = $"Del family {tag}", description = (string?)null })).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts", Part("DP-" + tag, site, fam));
        var r = await admin.DeleteAsync($"{S}/reference/values/{fam}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains($"Del family {tag}", body);
        Assert.Contains("is used by 1 part number in Parts and Items.", body);
        Assert.True(await HasAsync(admin, "product-families", fam));
    }

    [Fact]
    public async Task Family_uses_match_the_card_count_and_name_what_blocks_the_delete()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code = $"U{tag}", label = $"Use family {tag}", description = (string?)null })).JsonAsync()).Id();
        var empty = (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code = $"F{tag}", label = $"Form family {tag}", description = (string?)null })).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts", Part("UP-" + tag, site, fam));
        await admin.PostOkAsync($"{S}/parts", Part("UQ-" + tag, site, fam));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 2; i++)
                db.ImportedFiles.Add(new Sequfloh.Domain.Imports.ImportedFile { Number = $"IMP-{tag}-{i}", Title = $"Example filled form {i}", SourceFormat = "xlsx", OriginalFileName = "example.xlsx",
                    OriginalStorageKey = "example", SiteId = site, IsCompletedForm = true, FamilyId = empty, IsArchived = i == 1 });
            await db.SaveChangesAsync();
        }

        // the card counts distinct part numbers; the delete check counts the same way
        var card = (await admin.GetElAsync($"{S}/parts/family-counts")).EnumerateArray().First(c => c.GetProperty("typeId").ValueKind == System.Text.Json.JsonValueKind.String && c.GetProperty("typeId").GetGuid() == fam).GetProperty("count").GetInt32();
        var uses = await admin.GetElAsync($"{S}/reference/values/{fam}/uses");
        Assert.Equal(2, card);
        Assert.Equal(card, uses.GetProperty("partNumbers").GetInt32());
        Assert.Equal(2, uses.GetProperty("partRows").GetInt32());
        Assert.Equal(2, uses.GetProperty("parts").GetArrayLength());
        var r = await admin.DeleteAsync($"{S}/reference/values/{fam}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("is used by 2 part numbers in Parts and Items.", await r.Content.ReadAsStringAsync());

        // a family with no parts (card shows 0) but with imported forms says so
        var formUses = await admin.GetElAsync($"{S}/reference/values/{empty}/uses");
        Assert.Equal(0, formUses.GetProperty("partNumbers").GetInt32());
        Assert.Equal(2, formUses.GetProperty("files").GetArrayLength());
        var r2 = await admin.DeleteAsync($"{S}/reference/values/{empty}");
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Contains("is used by 2 imported files (2 Completed Validation / FAI; 1 archived).", await r2.Content.ReadAsStringAsync());
        Assert.True(await HasAsync(admin, "product-families", empty));
    }

    [Fact]
    public async Task Family_in_use_can_be_moved_to_another_family_and_deleted()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var from = (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code = $"M{tag}", label = $"Old family {tag}", description = (string?)null })).JsonAsync()).Id();
        var to = (await (await admin.PostOkAsync($"{S}/reference/product-families", new { code = $"N{tag}", label = $"New family {tag}", description = (string?)null })).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts", Part("MV-" + tag, site, from));
        Guid fileId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var f = new Sequfloh.Domain.Imports.ImportedFile { Number = $"MVF-{tag}", Title = "Example filled form", SourceFormat = "xlsx", OriginalFileName = "example.xlsx", OriginalStorageKey = "example", SiteId = site, IsCompletedForm = true, FamilyId = from };
            db.ImportedFiles.Add(f); await db.SaveChangesAsync(); fileId = f.Id;
        }
        var bad = await admin.PostAsync($"{S}/reference/values/{from}/move-and-delete", new { targetId = from });
        Assert.False(bad.IsSuccessStatusCode);
        await admin.PostOkAsync($"{S}/reference/values/{from}/move-and-delete", new { targetId = to });
        Assert.False(await HasAsync(admin, "product-families", from));
        var uses = await admin.GetElAsync($"{S}/reference/values/{to}/uses");
        Assert.Equal(1, uses.GetProperty("partNumbers").GetInt32());
        Assert.Equal(1, uses.GetProperty("files").GetArrayLength());
        using var scope2 = app.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal($"New family {tag}", await db2.Parts.Where(p => p.PartNumber == "MV-" + tag).Select(p => p.ProductFamily).SingleAsync());
        Assert.True(await db2.AuditEntries.AnyAsync(a => a.EntityId == from && a.Action == "ProductFamilyMoved" && a.UserName != null
            && a.Description == $"Moved 1 part and 1 imported file from product family Old family {tag} to New family {tag} before deleting Old family {tag}"));
        Assert.True(await db2.AuditEntries.AnyAsync(a => a.EntityId == from && a.Action == "ReferenceValueDeleted"));
    }

    [Fact]
    public async Task Deleted_seeded_value_is_not_seeded_again()
    {
        var admin = await app.AdminAsync();
        const string key = "lifecycle-actions";
        var v = (await admin.GetElAsync($"{S}/reference/{key}")).EnumerateArray().First(x => x.GetProperty("code").GetString() == "MARK_OBSOLETE");
        var id = v.GetProperty("id").GetGuid();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/reference/values/{id}")).StatusCode);
            await admin.PostOkAsync($"{S}/seed");
            Assert.DoesNotContain((await admin.GetElAsync($"{S}/reference/{key}")).EnumerateArray(), x => x.GetProperty("code").GetString() == "MARK_OBSOLETE");
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SequflohDeletedSeedValues.Where(x => x.ListKey == key && x.Code == "MARK_OBSOLETE").ExecuteDeleteAsync();
            await admin.PostOkAsync($"{S}/seed");
        }
    }

    [Fact]
    public async Task Delete_needs_the_manage_dropdowns_permission()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var id = (await (await admin.PostOkAsync($"{S}/reference/inspection-reasons", new { code = "", label = "Kept reason " + tag, description = (string?)null })).JsonAsync()).Id();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"{S}/reference/values/{id}")).StatusCode);
        Assert.True(await HasAsync(admin, "inspection-reasons", id));
    }
}
