using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>A validation form can be attached to a work order of any type under a typed number, and copies saved from it stay linked to the work order. Made-up data only.</summary>
[Collection("api")]
public sealed class WorkOrderValidationFormTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohStartTask, Permissions.SequflohCompleteTask, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles];

    [Fact]
    public async Task Any_work_order_type_takes_the_family_form_under_a_typed_number_and_saved_copies_stay_linked()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Planner, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "WV" + tag, label = "Widget " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nTorque,\n")), "file", $"Widget {tag} validation.csv" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        var blank = (await up.JsonAsync()).Id();
        Assert.True((await admin.PutAsync($"/api/sequfloh/validation-forms/{fam}", new { blankFormId = blank })).IsSuccessStatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var part = new Sequfloh.Domain.MasterData.Part { PartNumber = $"RW-{tag}", Name = "Example pump", Revision = "A", UnitOfMeasure = "EA", SiteId = site, IsRepairable = true };
        db.Parts.Add(part); await db.SaveChangesAsync();

        // a repair work order: the form is attached later, and only under a typed number
        var repair = (await (await c.PostOkAsync(T, new { type = "Repair", siteId = site, partId = part.Id, quantity = 3 })).JsonAsync()).Id();
        Assert.True((await c.GetElAsync($"{T}/{repair}")).GetProperty("canAttachForm").GetBoolean());
        var none = await c.PostAsync($"{T}/{repair}/validation-form", new { number = "", familyId = fam });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Type the number", await none.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{repair}/validation-form", new { number = $"RWF-{tag}-1" })).StatusCode); // no family chosen
        var ok = await c.PostAsync($"{T}/{repair}/validation-form", new { number = $"RWF-{tag}-1", familyId = fam });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        var rec = await c.GetElAsync($"{T}/{repair}");
        Assert.False(rec.GetProperty("canAttachForm").GetBoolean());
        var vf = rec.GetProperty("record").GetProperty("details").GetProperty("validationForm");
        Assert.Equal($"RWF-{tag}-1", vf.GetProperty("fileNumber").GetString());
        var first = Guid.Parse(vf.GetProperty("fileId").GetString()!);
        var f1 = await db.ImportedFiles.AsNoTracking().SingleAsync(x => x.Id == first);
        Assert.Equal(repair, f1.WorkOrderId); Assert.Equal(fam, f1.FamilyId); Assert.Equal(blank, f1.BlankFormId); Assert.Equal($"RW-{tag}", f1.PartNumber);
        Assert.False(await db.ImportedFiles.AnyAsync(x => x.Number.StartsWith($"Widget {tag} validation-")));

        // save as new copy keeps the title, family and work order; the number is typed and must be free
        var title = (await c.GetElAsync($"/api/imported-files/{first}")).GetProperty("title").GetString();
        var dup = await c.PostAsync($"/api/imported-files/{first}/save-as-copy", new { baseVersion = 1, number = $"RWF-{tag}-1", title, cells = Array.Empty<object>() });
        Assert.False(dup.IsSuccessStatusCode);
        var cp = await c.PostAsync($"/api/imported-files/{first}/save-as-copy", new { baseVersion = 1, number = $"RWF-{tag}-2", title, cells = Array.Empty<object>() });
        Assert.True(cp.IsSuccessStatusCode, await cp.Content.ReadAsStringAsync());
        var second = (await cp.JsonAsync()).Id();
        var again = await c.PostAsync($"/api/imported-files/{second}/save-as-copy", new { baseVersion = 1, number = $"RWF-{tag}-3", title, cells = Array.Empty<object>() });
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());
        var linked = await db.ImportedFiles.AsNoTracking().Where(x => x.WorkOrderId == repair).ToListAsync();
        Assert.Equal(3, linked.Count);
        Assert.All(linked, x => { Assert.Equal(fam, x.FamilyId); Assert.Equal(title, x.Title); });

        // a rebox work order takes the form at create when its number is typed
        var rebox = await (await c.PostOkAsync(T, new { type = "Rebox", siteId = site, familyId = fam, formNumber = $"RBF-{tag}-1" })).JsonAsync();
        var rf = Guid.Parse(rebox.GetProperty("validationForm").GetProperty("fileId").GetString()!);
        Assert.Equal(rebox.Id(), (await db.ImportedFiles.AsNoTracking().SingleAsync(x => x.Id == rf)).WorkOrderId);

        // a validation work order still waits for a typed number, then attaches its family's form
        var val = await (await c.PostOkAsync(T, new { type = "Validation", siteId = site, familyId = fam })).JsonAsync();
        Assert.Contains("Type the number", val.GetProperty("validationForm").GetProperty("message").GetString());
        var va = await c.PostAsync($"{T}/{val.Id()}/validation-form", new { number = $"VLF-{tag}-1" });
        Assert.True(va.IsSuccessStatusCode, await va.Content.ReadAsStringAsync());
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.EntityId == val.Id()).ToListAsync(), a => a.Action == "ValidationFormAttached" && a.UserName.Length > 0);
    }
}
