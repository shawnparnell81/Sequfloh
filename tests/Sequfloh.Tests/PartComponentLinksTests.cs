using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Parts list and Repair parts on a part, and parts added on a repair work order. Made-up data only.</summary>
[Collection("api")]
public sealed class PartComponentLinksTests(TestApp app)
{
    const string P = "/api/sequfloh/parts";
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView,
        Permissions.SequflohCreateRepairRecord, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask];

    async Task<(Guid Site, Guid Parent, Guid Other, string Tag, ApiClient C)> SetupAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Editor, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var parent = new Sequfloh.Domain.MasterData.Part { PartNumber = $"PL-{tag}", Name = "Example pump", Revision = "A", UnitOfMeasure = "EA", SiteId = site, IsRepairable = true, ProductFamily = "Example family" };
        var other = new Sequfloh.Domain.MasterData.Part { PartNumber = $"OS-{tag}", Name = "Example seal", Revision = "A", UnitOfMeasure = "EA", SiteId = site };
        db.Parts.AddRange(parent, other); await db.SaveChangesAsync();
        return (site, parent.Id, other.Id, tag, c);
    }

    static string[] Numbers(System.Text.Json.JsonElement e) => e.EnumerateArray().Select(x => x.GetProperty("partNumber").GetString()!).ToArray();

    [Fact]
    public async Task Parts_list_and_repair_parts_add_existing_and_typed_parts_without_duplicates_and_remove_only_unlinks()
    {
        var (site, parent, other, tag, c) = await SetupAsync();
        await c.PostOkAsync($"{P}/{parent}/links/parts-list", new { partId = other });
        await c.PostOkAsync($"{P}/{parent}/links/parts-list", new { partNumber = $" new-{tag} ", description = "Example gasket" });
        await c.PostOkAsync($"{P}/{parent}/links/parts-list", new { partId = other });
        await c.PostOkAsync($"{P}/{parent}/links/parts-list", new { partNumber = $"NEW-{tag}" });
        var list = await c.GetElAsync($"{P}/{parent}/links/parts-list");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Contains($"new-{tag}", Numbers(list));
        Assert.Empty((await c.GetElAsync($"{P}/{parent}/links/repair-parts")).EnumerateArray());

        await c.PostOkAsync($"{P}/{parent}/links/repair-parts", new { partNumber = $"RP-{tag}", description = "Example bearing" });
        Assert.Equal([$"RP-{tag}"], Numbers(await c.GetElAsync($"{P}/{parent}/links/repair-parts")));

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var made = await db.Parts.AsNoTracking().Where(x => x.SiteId == site && (x.PartNumber == $"new-{tag}" || x.PartNumber == $"RP-{tag}")).ToListAsync();
        Assert.Equal(2, made.Count);
        Assert.All(made, x => { Assert.True(x.IsRepairPart); Assert.Equal("Active", x.Status.ToString()); Assert.Equal("Example family", x.ProductFamily); });
        Assert.Equal(1, await db.Parts.CountAsync(x => x.SiteId == site && x.PartNumber.ToUpper() == $"NEW-{tag}"));

        var gasket = made.Single(x => x.PartNumber == $"new-{tag}").Id;
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"{P}/{parent}/links/parts-list/{gasket}")).StatusCode);
        Assert.Equal([$"OS-{tag}"], Numbers(await c.GetElAsync($"{P}/{parent}/links/parts-list")));
        Assert.True(await db.Parts.AnyAsync(x => x.Id == gasket));
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == parent).Select(a => a.Description).ToListAsync();
        Assert.True(audit.Count >= 4, string.Join(" | ", audit));
    }

    [Fact]
    public async Task Repair_work_order_add_part_creates_repair_part_links_it_to_repair_parts_and_respects_the_lock()
    {
        var (site, parent, other, tag, c) = await SetupAsync();
        var wo = (await (await c.PostOkAsync(T, new { type = "Repair", siteId = site, partId = parent, quantity = 1 })).JsonAsync()).Id();
        await c.PostOkAsync($"{T}/{wo}/repair-parts", new { partNumber = $"WO-{tag}", description = "Example impeller", quantity = 2, notes = "replace" });
        await c.PostOkAsync($"{T}/{wo}/repair-parts", new { partId = other, quantity = 1 });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{wo}/repair-parts", new { partNumber = $" wo-{tag}", quantity = 1 })).StatusCode);

        Assert.Equal([$"OS-{tag}", $"WO-{tag}"], Numbers(await c.GetElAsync($"{P}/{parent}/links/repair-parts")).OrderBy(x => x).ToArray());
        Assert.Empty((await c.GetElAsync($"{P}/{parent}/links/parts-list")).EnumerateArray());
        var repairView = await c.GetElAsync($"{P}?view=repair-parts&q=WO-{tag}&siteId={site}");
        Assert.Contains(repairView.GetProperty("items").EnumerateArray(), x => x.GetProperty("partNumber").GetString() == $"WO-{tag}");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await db.Parts.AsNoTracking().SingleAsync(x => x.Id == other)).IsRepairPart);
        var rec = await db.SequflohTrackingRecords.AsNoTracking().SingleAsync(x => x.Id == wo);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(rec.PartsRows)!.AsArray();
        Assert.Equal(2, rows.Count);
        var impeller = rows.Single(r => (string?)r!["replacementPartNumber"] == $"WO-{tag}")!;
        Assert.Equal(2m, (decimal)impeller["quantity"]!);

        // removing from the work order keeps the part on the list and in the Part Master
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"{T}/{wo}/repair-parts/{(string)impeller["id"]!}")).StatusCode);
        Assert.Equal(2, (await c.GetElAsync($"{P}/{parent}/links/repair-parts")).GetArrayLength());
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == wo && a.EntityType == "TrackingRecord").Select(a => a.Description).ToListAsync(), d => d.Contains($"WO-{tag}"));
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == other && a.EntityType == "Part").Select(a => a.Description).ToListAsync(), d => d.Contains("repair part", StringComparison.OrdinalIgnoreCase));

        var ver = (await c.GetElAsync($"{T}/{wo}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        await (await app.AdminAsync()).PostOkAsync($"{T}/{wo}/actions/cancel", new { operationId = Guid.NewGuid(), stateVersion = ver, reason = "Example cancel" });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"{T}/{wo}/repair-parts", new { partNumber = $"LATE-{tag}" })).StatusCode);
    }
}
