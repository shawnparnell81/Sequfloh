using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>
/// Customer assignment for parts that already exist. The customer list is reference data.
/// Confirming an assignment changes CustomerId only. Made-up parts, never a live catalog.
/// </summary>
[Collection("api")]
public sealed class CustomerAssignmentTests(TestApp app)
{
    const string S = "/api/sequfloh";
    static readonly string[] Editor = [Permissions.SequflohAccess, Permissions.SequflohEditParts, Permissions.MasterDataEdit, Permissions.MasterDataView];

    static object Part(string pn, Guid site, Guid? typeId, Dictionary<string, string?>? attributes = null, string? drawing = null) => new
    {
        part = new { partNumber = pn, name = "Example part " + pn, description = "Example strut", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
            hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
        execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = drawing, drawingRevision = drawing is null ? null : "B", specification = (string?)null },
        typeId, attributes,
    };

    static async Task<Guid> Family(ApiClient c) =>
        (await c.GetElAsync($"{S}/reference/product-families")).EnumerateArray().First(x => x.Str("label") == "Complete Strut Assembly (CSA)").Id();

    static async Task<Guid> Customer(ApiClient c, string label) =>
        (await c.GetElAsync($"{S}/reference/customers")).EnumerateArray().First(x => x.Str("label") == label).Id();

    static async Task<JsonElement> Row(ApiClient c, string partNumber)
    {
        var preview = await c.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(partNumber)}&pageSize=50");
        return preview.GetProperty("rows").EnumerateArray().First(r => r.Str("partNumber") == partNumber);
    }

    [Fact]
    public async Task Customers_are_reference_data_and_existing_parts_start_unassigned()
    {
        var admin = await app.AdminAsync();
        var lists = await admin.GetElAsync($"{S}/reference");
        var customers = lists.EnumerateArray().First(x => x.Str("key") == "customers");
        Assert.Contains("O'Reilly", customers.Str("description"));
        var values = (await admin.GetElAsync($"{S}/reference/customers")).EnumerateArray().Select(x => x.Str("label")).ToList();
        Assert.Contains("NAPA US", values);
        Assert.Contains("NAPA Canada", values);
        Assert.Contains("O'Reilly", values);
        Assert.Contains("AutoZone", values);
        Assert.DoesNotContain(values, v => v.Contains("SenSen", StringComparison.OrdinalIgnoreCase));

        var tag = Guid.NewGuid().ToString("N")[..6];
        var added = (await (await admin.PostOkAsync($"{S}/reference/customers", new { code = "", label = "Example Motors " + tag, description = (string?)null })).JsonAsync()).Id();
        await admin.PutOkAsync($"{S}/reference/values/{added}", new { code = "", label = "Example Motors renamed " + tag, description = "A customer an administrator added", isActive = true, isDefault = false, rowVersion = 0 });
        Assert.Contains((await admin.GetElAsync($"{S}/reference/customers")).EnumerateArray(), x => x.Str("label") == "Example Motors renamed " + tag);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/reference/values/{added}")).StatusCode);

        var site = await app.CreateSiteAsync();
        var fam = await Family(admin);
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part("NEW-" + tag, site, fam))).JsonAsync()).Id();
        var detail = (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part");
        Assert.Equal(id, detail.Id());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("customerId").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("customerName").ValueKind);
        var listed = (await admin.GetElAsync($"{S}/parts?q=NEW-{tag}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(id, listed.Id());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("customerId").ValueKind);
    }

    [Fact]
    public async Task Clear_assignment_changes_only_the_customer_and_repeating_it_is_harmless()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = await Family(admin);
        var pn = "OR-" + tag;
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam, new Dictionary<string, string?>
        {
            ["orNumber"] = pn, ["napaUs"] = "NU-" + tag, ["napaCa"] = "NC-" + tag, ["keyApplication"] = "RideMaster example",
        }, "9214-" + tag))).JsonAsync()).Id();
        Guid orderId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.Users.Select(u => u.Id).FirstAsync();
            var order = new SequflohTrackingRecord { Number = "WO-" + tag, Type = TrackingType.Fai, SiteId = site, PartId = id, PartNumber = pn, PartDescription = "kept description", Status = TrackingStatus.Completed, Findings = "{\"note\":\"scratch\"}" };
            db.SequflohTrackingRecords.Add(order);
            db.SequflohInspectionRequests.Add(new SequflohInspectionRequest { Number = "IR-" + tag, SiteId = site, PartId = id, PartNumber = pn, RequestedById = userId, RequestedByName = "Pat" });
            var folder = new DrawingFolder { Name = "Assign " + tag, SiteId = site };
            db.DrawingFolders.Add(folder);
            await db.SaveChangesAsync();
            var drawing = new Drawing { FolderId = folder.Id, Title = "Plate", DrawingNumber = "9214-" + tag, CurrentRevision = "B", SiteId = site };
            db.Drawings.Add(drawing);
            await db.SaveChangesAsync();
            db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = drawing.Id, PartId = id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            orderId = order.Id;
        }
        var before = await Snapshot(id);

        var row = await Row(admin, pn);
        Assert.Equal("Clear", row.Str("outcome"));
        Assert.Equal("O'Reilly", row.Str("proposedCustomer"));
        Assert.Equal(1, row.GetProperty("workOrders").GetInt32());
        Assert.Equal(1, row.GetProperty("fai").GetInt32());
        Assert.Equal(1, row.GetProperty("ncr").GetInt32());
        Assert.Equal(1, row.GetProperty("inspectionRequests").GetInt32());
        Assert.Equal(1, row.GetProperty("drawingLinks").GetInt32());
        Assert.Contains("WO-" + tag, row.Str("workOrderNumbers"));
        Assert.DoesNotContain("warning", row.Str("reason"), StringComparison.OrdinalIgnoreCase);

        var first = await (await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = pn, skipPartIds = Array.Empty<Guid>(), exceptions = Array.Empty<object>() })).JsonAsync();
        Assert.Equal(1, first.GetProperty("assigned").GetInt32());
        var after = await Snapshot(id);
        Assert.Equal(before.PartNumber, after.PartNumber);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.DrawingNumber, after.DrawingNumber);
        Assert.Equal(before.DrawingRevision, after.DrawingRevision);
        Assert.Equal(before.Attributes, after.Attributes);
        Assert.Equal(before.TypeId, after.TypeId);
        Assert.Equal(before.ProductFamily, after.ProductFamily);
        Assert.Equal(before.Upc, after.Upc);
        Assert.Equal(before.SiteId, after.SiteId);
        Assert.Equal(before.Revision, after.Revision);
        Assert.NotEqual(before.CustomerId, after.CustomerId);
        Assert.Equal(await Customer(admin, "O'Reilly"), after.CustomerId);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var order = await db.SequflohTrackingRecords.SingleAsync(x => x.Id == orderId);
            Assert.Equal(id, order.PartId);
            Assert.Equal(pn, order.PartNumber);
            Assert.Equal("kept description", order.PartDescription);
            Assert.Equal("WO-" + tag, order.Number);
            Assert.Equal(1, await db.DrawingPartLinks.CountAsync(l => l.PartId == id));
            Assert.Equal(1, await db.SequflohInspectionRequests.CountAsync(x => x.PartId == id && x.Number == "IR-" + tag));
            var audit = await db.AuditEntries.SingleAsync(a => a.EntityId == id && a.Action == "CustomerAssigned");
            Assert.False(string.IsNullOrWhiteSpace(audit.UserName));
            Assert.NotEqual(default, audit.OccurredAt);
            Assert.Equal($"Assigned O'Reilly to part {pn}. Part ID and existing links kept.", audit.Description);
        }

        var detail = (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part");
        Assert.Equal(id, detail.Id());
        Assert.Equal("O'Reilly", detail.Str("customerName"));
        var csv = (await (await admin.Http.GetAsync($"{S}/parts/export?q={pn}")).Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var head = csv.Split("\r\n")[0].Split(',');
        Assert.Equal("Customer", head[4]);
        Assert.Equal("O'Reilly", csv.Split("\r\n")[1].Split(',')[4]);

        var savedRow = await Row(admin, pn);
        Assert.Equal("Assigned", savedRow.Str("outcome"));
        Assert.Equal("O'Reilly", savedRow.Str("customerName"));
        Assert.Equal(JsonValueKind.Null, savedRow.GetProperty("proposedCustomer").ValueKind);
        Assert.Contains("Already O'Reilly", savedRow.Str("reason"));
        var counts = await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(pn)}");
        Assert.Equal(0, counts.GetProperty("clear").GetInt32());
        Assert.Equal(0, counts.GetProperty("unassigned").GetInt32());
        Assert.Equal(0, counts.GetProperty("ambiguous").GetInt32());
        Assert.Equal(1, counts.GetProperty("assigned").GetInt32());

        var second = await admin.PostAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = pn, skipPartIds = Array.Empty<Guid>(), exceptions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var again = await Snapshot(id);
        Assert.Equal(after.CustomerId, again.CustomerId);
        Assert.Equal(after.PartNumber, again.PartNumber);
        Assert.Equal(after.Attributes, again.Attributes);
        using var end = app.Services.CreateScope();
        var dbEnd = end.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await dbEnd.AuditEntries.CountAsync(a => a.EntityId == id && a.Action == "CustomerAssigned"));
    }

    [Fact]
    public async Task Ambiguous_alias_and_supplier_numbers_stay_unassigned_and_a_shared_drawing_is_not_a_conflict()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = await Family(admin);
        var shared = "9214-0800-" + tag;
        var both = "BOTH-" + tag;
        var supplier = "SS-" + tag;
        var frontLeft = "FL-" + tag;
        var frontRight = "FR-" + tag;
        await admin.PostOkAsync($"{S}/parts", Part(both, site, fam, new Dictionary<string, string?> { ["orNumber"] = both, ["napaUs"] = both }));
        await admin.PostOkAsync($"{S}/parts", Part(supplier, site, fam, new Dictionary<string, string?> { ["orNumber"] = "OR-" + tag, ["napaUs"] = "NU-" + tag, ["dmaPn"] = supplier }, shared));
        var left = (await (await admin.PostOkAsync($"{S}/parts", Part(frontLeft, site, fam, new Dictionary<string, string?> { ["orNumber"] = frontLeft, ["position"] = "FL" }, shared))).JsonAsync()).Id();
        var right = (await (await admin.PostOkAsync($"{S}/parts", Part(frontRight, site, fam, new Dictionary<string, string?> { ["orNumber"] = frontRight, ["position"] = "FR" }, shared))).JsonAsync()).Id();

        var ambiguous = await Row(admin, both);
        Assert.Equal("Ambiguous", ambiguous.Str("outcome"));
        Assert.Equal(JsonValueKind.Null, ambiguous.GetProperty("proposedCustomerId").ValueKind);
        var unmatched = await Row(admin, supplier);
        Assert.Equal("Unassigned", unmatched.Str("outcome"));
        Assert.Contains("alias", unmatched.Str("reason"), StringComparison.OrdinalIgnoreCase);
        var fl = await Row(admin, frontLeft);
        var fr = await Row(admin, frontRight);
        Assert.Equal("Clear", fl.Str("outcome"));
        Assert.Equal("Clear", fr.Str("outcome"));
        Assert.Equal(shared, fl.Str("drawingNumber"));
        Assert.Equal(shared, fr.Str("drawingNumber"));
        Assert.DoesNotContain("conflict", fl.Str("reason"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warning", fr.Str("reason"), StringComparison.OrdinalIgnoreCase);

        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = tag, skipPartIds = Array.Empty<Guid>(), exceptions = Array.Empty<object>() });
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await db.Parts.Where(p => p.PartNumber == both).Select(p => p.CustomerId).SingleAsync());
        Assert.Null(await db.Parts.Where(p => p.PartNumber == supplier).Select(p => p.CustomerId).SingleAsync());
        var oreilly = await Customer(admin, "O'Reilly");
        Assert.Equal(oreilly, await db.Parts.Where(p => p.Id == left).Select(p => p.CustomerId).SingleAsync());
        Assert.Equal(oreilly, await db.Parts.Where(p => p.Id == right).Select(p => p.CustomerId).SingleAsync());
        Assert.Equal(shared, await db.Parts.Where(p => p.Id == left).Select(p => p.DrawingNumber).SingleAsync());
        Assert.Equal(shared, await db.Parts.Where(p => p.Id == right).Select(p => p.DrawingNumber).SingleAsync());
    }

    [Fact]
    public async Task An_administrator_can_add_a_customer_and_confirm_an_exception()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = await Family(admin);
        var nissan = (await (await admin.PostOkAsync($"{S}/reference/customers", new { code = "", label = "Nissan " + tag, description = (string?)null })).JsonAsync());
        Assert.Equal("NISSAN_" + tag, nissan.Str("code"));
        // the seeded code NISSAN is what the Nissan VA # column matches; this added customer uses its own code and is chosen by hand
        var pn = "OPEN-" + tag;
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam, new Dictionary<string, string?> { ["nissanVa"] = "VA-" + tag }))).JsonAsync()).Id();
        Assert.Equal("Unassigned", (await Row(admin, pn)).Str("outcome"));
        var autozone = await Customer(admin, "AutoZone");
        var result = await (await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = id, customerId = autozone } } })).JsonAsync();
        Assert.Equal(1, result.GetProperty("assigned").GetInt32());
        Assert.Equal("AutoZone", (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part").Str("customerName"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal($"Assigned AutoZone to part {pn}. Part ID and existing links kept.", await db.AuditEntries.Where(a => a.EntityId == id && a.Action == "CustomerAssigned").Select(a => a.Description).SingleAsync());
            Assert.Equal(pn, await db.Parts.Where(p => p.Id == id).Select(p => p.PartNumber).SingleAsync());
        }

        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = id, customerId = (Guid?)null } } });
        Assert.Equal(JsonValueKind.Null, (await admin.GetElAsync($"{S}/parts/{id}")).GetProperty("part").GetProperty("customerId").ValueKind);
        using var end = app.Services.CreateScope();
        var dbEnd = end.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains($"Cleared the customer on part {pn} (was AutoZone). Part ID and existing links kept.", await dbEnd.AuditEntries.Where(a => a.EntityId == id && a.Action == "CustomerCleared").Select(a => a.Description).ToListAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/reference/values/{nissan.Id()}")).StatusCode);
    }

    [Fact]
    public async Task A_customer_in_use_cannot_be_deleted_and_a_deactivated_customer_cannot_be_assigned()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = await Family(admin);
        var customer = (await (await admin.PostOkAsync($"{S}/reference/customers", new { code = "", label = "Held customer " + tag, description = (string?)null })).JsonAsync()).Id();
        var pn = "HELD-" + tag;
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = id, customerId = customer } } });

        var blocked = await admin.DeleteAsync($"{S}/reference/values/{customer}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Held customer " + tag, await blocked.Content.ReadAsStringAsync());
        var uses = await admin.GetElAsync($"{S}/reference/values/{customer}/uses");
        Assert.Equal(1, uses.GetProperty("partNumbers").GetInt32());

        await admin.PostOkAsync($"{S}/reference/values/{customer}/deactivate");
        var other = (await (await admin.PostOkAsync($"{S}/parts", Part("HELD2-" + tag, site, fam))).JsonAsync()).Id();
        var denied = await admin.PostAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = other, customerId = customer } } });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("deactivated", await denied.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(customer, await db.Parts.Where(p => p.Id == id).Select(p => p.CustomerId).SingleAsync());
            Assert.Null(await db.Parts.Where(p => p.Id == other).Select(p => p.CustomerId).SingleAsync());
        }

        await admin.PostOkAsync($"{S}/reference/values/{customer}/activate");
        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = id, customerId = (Guid?)null } } });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{S}/reference/values/{customer}")).StatusCode);
    }

    [Fact]
    public async Task Assignment_follows_part_edit_permission_and_site_scope()
    {
        var home = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var mine = (await (await admin.PostOkAsync($"{S}/parts", Part("MINE-" + tag, home, fam, new Dictionary<string, string?> { ["orNumber"] = "MINE-" + tag }))).JsonAsync()).Id();
        var theirs = (await (await admin.PostOkAsync($"{S}/parts", Part("THEIRS-" + tag, other, fam, new Dictionary<string, string?> { ["orNumber"] = "THEIRS-" + tag }))).JsonAsync()).Id();
        var oreilly = await Customer(admin, "O'Reilly");

        var reader = (await app.CreateUserAsync([Permissions.SequflohAccess], home)).Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{S}/parts/customers/assignment")).StatusCode);

        var half = (await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohEditParts], home)).Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await half.PostAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = tag })).StatusCode);

        var (_, local) = await app.CreateUserAsync(Editor, home);
        var preview = await local.GetElAsync($"{S}/parts/customers/assignment?q={tag}&pageSize=50");
        var numbers = preview.GetProperty("rows").EnumerateArray().Select(r => r.Str("partNumber")).ToList();
        Assert.Contains("MINE-" + tag, numbers);
        Assert.DoesNotContain("THEIRS-" + tag, numbers);
        var leaked = await local.PostAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = theirs, customerId = oreilly } } });
        Assert.Equal(HttpStatusCode.Forbidden, leaked.StatusCode);
        await local.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = "MINE-" + tag });

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(oreilly, await db.Parts.Where(p => p.Id == mine).Select(p => p.CustomerId).SingleAsync());
        Assert.Null(await db.Parts.Where(p => p.Id == theirs).Select(p => p.CustomerId).SingleAsync());
        Assert.Equal("THEIRS-" + tag, await db.Parts.Where(p => p.Id == theirs).Select(p => p.PartNumber).SingleAsync());
    }

    [Fact]
    public async Task Adding_Nissan_lets_a_matching_primary_number_suggest_that_customer()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        Guid nissan;
        try
        {
            nissan = (await (await admin.PostOkAsync($"{S}/reference/customers", new { code = "NISSAN", label = "Nissan", description = (string?)null })).JsonAsync()).Id();
        }
        catch (InvalidOperationException)
        {
            nissan = await Customer(admin, "Nissan");
        }
        var pn = "VA-" + tag;
        try
        {
            var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam, new Dictionary<string, string?> { ["nissanVa"] = pn, ["napaUs"] = "NU-" + tag }))).JsonAsync()).Id();
            var row = await Row(admin, pn);
            Assert.Equal("Clear", row.Str("outcome"));
            Assert.Equal("Nissan", row.Str("proposedCustomer"));
            await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = pn });
            Assert.Equal(nissan, await Snapshot(id).ContinueWith(t => t.Result.CustomerId));
        }
        finally
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Parts.Where(p => p.PartNumber == pn).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, (Guid?)null));
            var value = await db.SequflohReferenceValues.FirstOrDefaultAsync(v => v.Id == nissan);
            if (value is not null) db.SequflohReferenceValues.Remove(value);
            if (!await db.SequflohDeletedSeedValues.AnyAsync(x => x.ListKey == "customers" && x.Code == "NISSAN"))
                db.SequflohDeletedSeedValues.Add(new SequflohDeletedSeedValue { ListKey = "customers", Code = "NISSAN" });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task A_saved_customer_is_not_unassigned_and_a_different_suggestion_is_not_overwritten()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var autozone = await Customer(admin, "AutoZone");
        var oreilly = await Customer(admin, "O'Reilly");

        var plainPn = "PLAIN-" + tag;
        var plain = (await (await admin.PostOkAsync($"{S}/parts", Part(plainPn, site, fam))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = plain, customerId = autozone } } });
        var plainRow = await Row(admin, plainPn);
        Assert.Equal("Assigned", plainRow.Str("outcome"));
        Assert.Equal("AutoZone", plainRow.Str("customerName"));
        Assert.Equal(JsonValueKind.Null, plainRow.GetProperty("proposedCustomerId").ValueKind);
        var plainPreview = await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(plainPn)}");
        Assert.Equal(1, plainPreview.GetProperty("assigned").GetInt32());
        Assert.Equal(0, plainPreview.GetProperty("unassigned").GetInt32());
        Assert.Equal(0, plainPreview.GetProperty("ambiguous").GetInt32());
        Assert.Equal(0, plainPreview.GetProperty("clear").GetInt32());
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(plainPn)}&outcome=Unassigned")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(plainPn)}&outcome=Ambiguous")).GetProperty("total").GetInt32());
        var assignedTab = await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(plainPn)}&outcome=Assigned");
        Assert.Equal(plainPn, assignedTab.GetProperty("rows").EnumerateArray().Single().Str("partNumber"));

        var bothPn = "BOTH-" + tag;
        var both = (await (await admin.PostOkAsync($"{S}/parts", Part(bothPn, site, fam, new Dictionary<string, string?> { ["orNumber"] = bothPn, ["napaUs"] = bothPn }))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = both, customerId = autozone } } });
        Assert.Equal("Assigned", (await Row(admin, bothPn)).Str("outcome"));
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(bothPn)}&outcome=Ambiguous")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(bothPn)}&outcome=Unassigned")).GetProperty("total").GetInt32());

        var diffPn = "DIFF-" + tag;
        var diff = (await (await admin.PostOkAsync($"{S}/parts", Part(diffPn, site, fam, new Dictionary<string, string?> { ["orNumber"] = diffPn }))).JsonAsync()).Id();
        await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = diff, customerId = autozone } } });
        var diffRow = await Row(admin, diffPn);
        Assert.Equal("Different", diffRow.Str("outcome"));
        Assert.Equal("AutoZone", diffRow.Str("customerName"));
        Assert.Equal("O'Reilly", diffRow.Str("proposedCustomer"));
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(diffPn)}&outcome=Unassigned")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(diffPn)}&outcome=Clear")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetElAsync($"{S}/parts/customers/assignment?q={Uri.EscapeDataString(diffPn)}&outcome=Assigned")).GetProperty("total").GetInt32());

        var leftAlone = await admin.PostAsync($"{S}/parts/customers/assignment", new { assignClear = true, q = diffPn, skipPartIds = Array.Empty<Guid>(), exceptions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, leftAlone.StatusCode);
        Assert.Equal(autozone, (await Snapshot(diff)).CustomerId);

        var chosen = await (await admin.PostOkAsync($"{S}/parts/customers/assignment", new { assignClear = false, exceptions = new[] { new { partId = diff, customerId = oreilly } } })).JsonAsync();
        Assert.Equal(1, chosen.GetProperty("assigned").GetInt32());
        Assert.Equal(oreilly, (await Snapshot(diff)).CustomerId);
        var switched = await Row(admin, diffPn);
        Assert.Equal("Assigned", switched.Str("outcome"));
        Assert.Equal("O'Reilly", switched.Str("customerName"));
        Assert.Equal(JsonValueKind.Null, switched.GetProperty("proposedCustomer").ValueKind);
    }

    [Fact]
    public async Task The_assignment_export_keeps_part_ids_aliases_and_job_numbers()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var fam = await Family(admin);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var pn = "MAP-" + tag;
        var id = (await (await admin.PostOkAsync($"{S}/parts", Part(pn, site, fam, new Dictionary<string, string?> { ["orNumber"] = pn, ["napaUs"] = "NU-" + tag }, "DWG-" + tag))).JsonAsync()).Id();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SequflohTrackingRecords.Add(new SequflohTrackingRecord { Number = "JOB-" + tag, Type = TrackingType.Validation, SiteId = site, PartId = id, PartNumber = pn, Status = TrackingStatus.Assigned });
            await db.SaveChangesAsync();
        }
        var csv = (await (await admin.Http.GetAsync($"{S}/parts/customers/assignment/export?q={pn}")).Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Part ID,Part number,", lines[0]);
        Assert.Contains("Proposed customer", lines[0]);
        Assert.Contains("Work order numbers", lines[0]);
        Assert.DoesNotContain("Brand", lines[0]);
        Assert.Contains(id.ToString(), lines[1]);
        Assert.Contains(pn, lines[1]);
        Assert.Contains("O'Reilly", lines[1]);
        Assert.Contains("NU-" + tag, lines[1]);
        Assert.Contains("JOB-" + tag, lines[1]);
        Assert.Contains("Clear", lines[1]);
    }

    async Task<PartSnap> Snapshot(Guid id)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Parts.AsNoTracking().Where(p => p.Id == id).Select(p => new PartSnap(p.Id, p.PartNumber, p.Name, p.DrawingNumber, p.DrawingRevision, p.Attributes, p.TypeId, p.ProductFamily, p.Upc, p.SiteId, p.Revision, p.CustomerId)).SingleAsync();
    }

    sealed record PartSnap(Guid Id, string PartNumber, string Name, string? DrawingNumber, string? DrawingRevision, string? Attributes, Guid? TypeId, string? ProductFamily, string? Upc, Guid SiteId, string Revision, Guid? CustomerId);
}
