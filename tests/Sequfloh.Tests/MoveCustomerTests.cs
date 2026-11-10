using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Moving a part to another customer changes only the customer. A part number the target already has in that product line and site is skipped.</summary>
[Collection("api")]
public sealed class MoveCustomerTests(TestApp app)
{
    const string M = "/api/sequfloh/parts/move-customer";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    async Task<(Guid Site, Guid Line, Guid Oreilly, ApiClient Admin)> ReadyAsync()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var line = (await admin.GetElAsync("/api/sequfloh/reference/product-families")).EnumerateArray().First(x => x.Str("label") == "Complete Strut Assembly (CSA)").Id();
        var oreilly = (await admin.GetElAsync("/api/sequfloh/reference/customers")).EnumerateArray().First(x => x.Str("label") == "O'Reilly").Id();
        return (site, line, oreilly, admin);
    }

    static Part NewPart(string pn, Guid site, Guid line, Guid? customer) => new()
    {
        PartNumber = pn, Name = "Example " + pn, Description = "Example strut", Revision = "A", UnitOfMeasure = "EA", SiteId = site, TypeId = line,
        ProductFamily = "Complete Strut Assembly (CSA)", CustomerId = customer, DrawingNumber = "DWG-" + pn,
    };

    [Fact]
    public async Task One_part_moves_and_keeps_its_id_drawing_and_links()
    {
        var (site, line, oreilly, admin) = await ReadyAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid id, drawingId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var part = NewPart("ONE-" + tag, site, line, null);
            db.Parts.Add(part);
            await db.SaveChangesAsync();
            db.PartRevisions.Add(new PartRevision { PartId = part.Id, Revision = "A", Reason = "Initial", EffectiveOn = DateOnly.FromDateTime(DateTime.UtcNow) });
            var folder = new DrawingFolder { Name = "Move " + tag, SiteId = site };
            db.DrawingFolders.Add(folder);
            await db.SaveChangesAsync();
            var drawing = new Drawing { FolderId = folder.Id, Title = "Bracket", DrawingNumber = "DWG-ONE-" + tag, CurrentRevision = "A", SiteId = site };
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync();
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = drawing.Id, PartId = part.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            id = part.Id; drawingId = drawing.Id;
        }

        var plan = await (await admin.PostOkAsync(M, new { mode = "ids", partIds = new[] { id }, customerId = oreilly, toUnassigned = false })).JsonAsync();
        Assert.Equal(1, plan.Int("move"));
        Assert.Equal(0, plan.Int("conflicts"));
        Assert.Equal("O'Reilly", plan.Str("target"));

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var part = await db.Parts.AsNoTracking().SingleAsync(p => p.Id == id);
            Assert.Equal(oreilly, part.CustomerId);
            Assert.Equal("ONE-" + tag, part.PartNumber);
            Assert.Equal("DWG-ONE-" + tag, part.DrawingNumber);
            Assert.Equal("A", part.Revision);
            Assert.Equal(line, part.TypeId);
            Assert.True(await db.PartRevisions.AnyAsync(r => r.PartId == id && r.Revision == "A"));
            Assert.True(await db.DrawingPartLinks.AnyAsync(l => l.PartId == id && l.DrawingId == drawingId));
            var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.EntityId == id && a.Action == "Moved customer");
            Assert.False(string.IsNullOrWhiteSpace(audit.UserName));
            Assert.NotNull(audit.UserId);
            Assert.InRange(audit.OccurredAt, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Contains("from Unassigned to O'Reilly", audit.Description);
            Assert.Contains("ONE-" + tag, audit.Description);
        }
    }

    [Fact]
    public async Task A_filter_moves_every_matching_part_in_one_request_and_skips_a_conflict()
    {
        var (site, line, oreilly, admin) = await ReadyAsync();
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 1; i <= 30; i++) db.Parts.Add(NewPart($"BULK-{tag}-{i:00}", site, line, null));
            db.Parts.Add(NewPart($"BULK-{tag}-01", site, line, oreilly));
            await db.SaveChangesAsync();
        }
        var denied = await viewer.Http.PostAsJsonAsync(M + "/preview", new { mode = "filter", q = "BULK-" + tag, customer = "unassigned", partType = line.ToString(), customerId = oreilly });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var body = new { mode = "filter", q = "BULK-" + tag, customer = "unassigned", partType = line.ToString(), customerId = oreilly, toUnassigned = false };
        var preview = await (await admin.PostOkAsync(M + "/preview", body)).JsonAsync();
        Assert.Equal(29, preview.Int("move"));
        Assert.Equal(1, preview.Int("conflicts"));
        Assert.Contains(preview.GetProperty("conflictRows").EnumerateArray(), x => x.Str("partNumber") == $"BULK-{tag}-01");
        Assert.Contains(preview.GetProperty("from").EnumerateArray(), x => x.Str("customer") == "Unassigned" && x.Int("count") == 29);

        var done = await (await admin.PostOkAsync(M, body)).JsonAsync();
        Assert.Equal(29, done.Int("move"));
        Assert.Equal(1, done.Int("conflicts"));

        using var check = app.Services.CreateScope();
        var db2 = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var parts = await db2.Parts.AsNoTracking().Where(p => p.PartNumber.StartsWith("BULK-" + tag)).ToListAsync();
        Assert.Equal(31, parts.Count);
        Assert.Equal(30, parts.Count(p => p.CustomerId == oreilly));
        var skipped = parts.Single(p => p.CustomerId == null);
        Assert.Equal($"BULK-{tag}-01", skipped.PartNumber);
        Assert.Equal("DWG-BULK-" + tag + "-01", skipped.DrawingNumber);
        var summary = await db2.AuditEntries.AsNoTracking().SingleAsync(a => a.EntityType == "PartCustomerMove" && a.Description.Contains("BULK-" + tag) == false && a.Description.Contains($"Moved 29 parts to O'Reilly"));
        Assert.False(string.IsNullOrWhiteSpace(summary.UserName));
        Assert.Contains("29 from Unassigned", summary.Description);
        Assert.Contains("1 skipped", summary.Description);
        Assert.Equal(29, await db2.AuditEntries.CountAsync(a => a.Action == "Moved customer" && a.EntityType == "Part" && a.Description.Contains("BULK-" + tag)));
    }
}
