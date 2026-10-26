using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Test work orders, WO-TST-{family}{yy}-001, with the part's drawing and a validation form. Made-up data only.</summary>
[Collection("api")]
public sealed class TestWorkOrderTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task Test_work_orders_number_from_the_family_link_the_part_drawing_and_take_a_validation_form()
    {
        Assert.Equal(9, (int)TrackingType.Test);
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = $"T{tag}", label = $"Test family {tag}", isActive = true, isDefault = false })).JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/reference/values/{fam}/number-code", new { code = $"csa{tag}" })).IsSuccessStatusCode);
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohImportFiles, Permissions.SequflohViewImportedFiles, Permissions.SequflohViewDrawings], site);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var part = new Sequfloh.Domain.MasterData.Part { PartNumber = $"TP-{tag}", Name = "Example pump", Revision = "A", UnitOfMeasure = "EA", SiteId = site, TypeId = fam };
        var other = new Sequfloh.Domain.MasterData.Part { PartNumber = $"TP-{tag}X", Name = "Example pump", Revision = "A", UnitOfMeasure = "EA", SiteId = site, TypeId = fam };
        db.Parts.AddRange(part, other);
        var folder = new DrawingFolder { Name = "Test drawings " + tag, SiteId = site };
        db.DrawingFolders.Add(folder); await db.SaveChangesAsync();
        var dwg = new Drawing { FolderId = folder.Id, Title = $"TP-{tag} drawing", SiteId = site };
        db.Drawings.Add(dwg); db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = dwg.Id, PartId = part.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var yy = (DateTime.Now.Year % 100).ToString("00");
        Assert.Equal($"WO-TST-CSA{tag}{yy}-001", (await c.GetElAsync($"{T}/next-number?type=Test&familyId={fam}&siteId={site}")).GetProperty("number").GetString());
        async Task<System.Text.Json.JsonElement> Create(object body) { var id = (await (await c.PostOkAsync(T, body)).JsonAsync()).Id(); return (await c.GetElAsync($"{T}/{id}")).GetProperty("record"); }
        var r1 = await Create(new { type = "Test", siteId = site, partId = part.Id, familyId = fam, number = (string?)null });
        Assert.Equal($"WO-TST-CSA{tag}{yy}-001", r1.GetProperty("number").GetString());
        Assert.Equal("Test", r1.GetProperty("type").GetString());
        // the drawing of the exact part number is linked, nothing for a similar number
        var drawings = (await c.GetElAsync($"/api/drawings/for-record/{r1.Id()}")).EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([dwg.Id], drawings);
        var r2 = await Create(new { type = "Test", siteId = site, partId = other.Id, number = (string?)null });
        Assert.Equal($"WO-TST-CSA{tag}{yy}-002", r2.GetProperty("number").GetString());
        Assert.Empty((await c.GetElAsync($"/api/drawings/for-record/{r2.Id()}")).EnumerateArray());
        Assert.Equal(2, (await c.GetElAsync($"{T}?folder=test&siteId={site}&pageSize=50")).GetProperty("items").GetArrayLength());

        // a validation form attaches under a typed number
        var up = await c.Http.PostAsync("/api/imported-files", new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,\n")), "file", $"Test family {tag} form.csv" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } });
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        Assert.True((await admin.PutAsync($"/api/sequfloh/validation-forms/{fam}", new { blankFormId = (await up.JsonAsync()).Id() })).IsSuccessStatusCode);
        var att = await c.PostAsync($"{T}/{r2.Id()}/validation-form", new { number = $"TSF-{tag}-1", familyId = fam });
        Assert.True(att.IsSuccessStatusCode, await att.Content.ReadAsStringAsync());
        var fileId = Guid.Parse((await c.GetElAsync($"{T}/{r2.Id()}")).GetProperty("record").GetProperty("details").GetProperty("validationForm").GetProperty("fileId").GetString()!);
        Assert.Equal(r2.Id(), (await db.ImportedFiles.AsNoTracking().SingleAsync(x => x.Id == fileId)).WorkOrderId);

        // the code is edited in Tracking settings
        var (_, settingsAdmin) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohManageSettings]);
        var tracking = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>((await settingsAdmin.GetElAsync("/api/sequfloh/settings")).GetProperty("tracking").GetRawText())!;
        Assert.Equal("TST", tracking["testNumberCode"]?.ToString());
        try
        {
            tracking["testNumberCode"] = "lab";
            Assert.Equal(HttpStatusCode.NoContent, (await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 })).StatusCode);
            Assert.Equal($"WO-LAB-CSA{tag}{yy}-001", (await Create(new { type = "Test", siteId = site, familyId = fam, number = (string?)null })).GetProperty("number").GetString());
        }
        finally
        {
            tracking["testNumberCode"] = "TST";
            await settingsAdmin.PutAsync("/api/sequfloh/settings/tracking", new { value = tracking, rowVersion = 0 });
        }
        // validation instructions are offered, repair work instructions are not
        Assert.DoesNotContain(Sequfloh.Domain.Execution.WorkInstructionType.RepairWork, Sequfloh.Infrastructure.Modules.Execution.SequflohTrackingService.InstructionTypes(TrackingType.Test));
        Assert.Contains(Sequfloh.Domain.Execution.WorkInstructionType.Validation, Sequfloh.Infrastructure.Modules.Execution.SequflohTrackingService.InstructionTypes(TrackingType.Test));
    }
}
