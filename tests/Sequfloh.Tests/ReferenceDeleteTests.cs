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
        Assert.Contains("is used by 1 part. Deactivate it instead.", body);
        Assert.True(await HasAsync(admin, "product-families", fam));
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
