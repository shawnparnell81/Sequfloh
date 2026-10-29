using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Test work orders: Attach validation form? Yes / No (default No). Yes uses the family's confirmed validation form, exactly like Validation / FAI. Made-up data only.</summary>
[Collection("api")]
public sealed class TestOrderFormChoiceTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohViewImportedFiles, Permissions.SequflohImportFiles, Permissions.SequflohViewReports];

    [Fact]
    public async Task No_is_the_default_Yes_attaches_the_confirmed_form_and_an_attached_copy_cannot_be_switched_to_No()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var (_, c) = await app.CreateUserAsync(Tech, site);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var fam = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "TF" + tag, label = "Tester " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();
        var bare = (await (await admin.PostOkAsync("/api/sequfloh/reference/product-families", new { code = "TN" + tag, label = "Unformed " + tag, isActive = true, isDefault = false })).JsonAsync()).Id();
        var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("Check,Result\nPressure,\n")), "file", $"Tester {tag} validation.csv" }, { new StringContent(site.ToString()), "siteId" }, { new StringContent("true"), "blankForm" } };
        var up = await c.Http.PostAsync("/api/imported-files", form);
        Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
        Assert.True((await admin.PutAsync($"/api/sequfloh/validation-forms/{fam}", new { blankFormId = (await up.JsonAsync()).Id() })).IsSuccessStatusCode);
        Guid part, bpart;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Part { PartNumber = "TFC" + tag, Name = "Example tester", SiteId = site, Status = MasterStatus.Active, TypeId = fam, ProductFamily = "Tester " + tag };
            var b = new Part { PartNumber = "TFN" + tag, Name = "Example unformed", SiteId = site, Status = MasterStatus.Active, TypeId = bare, ProductFamily = "Unformed " + tag };
            db.Parts.AddRange(p, b); await db.SaveChangesAsync(); part = p.Id; bpart = b.Id;
        }
        async Task<(Guid Id, JsonElement Res)> Create(object body) { var r = await (await c.PostOkAsync(T, body)).JsonAsync(); return (r.Id(), r); }
        JsonElement Details(JsonElement rec) => rec.GetProperty("record").GetProperty("details");

        // default No: nothing attached, block absent, printout says No
        var (no, _) = await Create(new { type = "Test", number = "TFC-N-" + tag, siteId = site, familyId = fam, partId = part });
        var rn = await c.GetElAsync($"{T}/{no}");
        Assert.False(Details(rn).TryGetProperty("validationForm", out _));
        Assert.False(rn.GetProperty("canAttachForm").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"{T}/{no}/validation-form", new { number = "X-" + tag })).StatusCode);
        Assert.Contains("No - not attached", Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{no}/print")).Content.ReadAsByteArrayAsync()));

        // Yes with a typed number: the confirmed copy is attached
        var (yes, res) = await Create(new { type = "Test", number = "TFC-Y-" + tag, siteId = site, familyId = fam, partId = part, attachValidationForm = true, formNumber = $"TFF-{tag}-1" });
        Assert.Equal($"TFF-{tag}-1", res.GetProperty("validationForm").GetProperty("fileNumber").GetString());
        Assert.Contains($"TFF-{tag}-1", (await (await c.GetAsync($"{T}/{yes}/job/report")).Content.ReadAsStringAsync()));
        var sw = await c.PutAsync($"{T}/{yes}/validation-form/choice", new { attach = false });
        Assert.Equal(HttpStatusCode.Conflict, sw.StatusCode);
        Assert.Contains("never detached", await sw.Content.ReadAsStringAsync());

        // no confirmed form for the family: message, nothing attached
        var (nf, nres) = await Create(new { type = "Test", number = "TFN-Y-" + tag, siteId = site, familyId = bare, partId = bpart, attachValidationForm = true });
        Assert.Equal("No validation form set up for this product family.", nres.GetProperty("validationForm").GetProperty("message").GetString());

        // switch No -> Yes on the open order, then attach under a typed number; Yes -> No allowed while nothing is attached
        await c.PutOkAsync($"{T}/{no}/validation-form/choice", new { attach = true });
        Assert.True((await c.GetElAsync($"{T}/{no}")).GetProperty("canAttachForm").GetBoolean());
        await c.PutOkAsync($"{T}/{nf}/validation-form/choice", new { attach = false });
        Assert.False(Details(await c.GetElAsync($"{T}/{nf}")).TryGetProperty("validationForm", out _));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = await db.AuditEntries.AsNoTracking().Where(x => x.Action == "ValidationFormChoice" && (x.EntityId == no || x.EntityId == nf || x.EntityId == yes)).Select(x => x.Description).ToListAsync();
            Assert.Contains(a, x => x!.Contains("Attach validation form? No"));
            Assert.Contains(a, x => x!.Contains($"TFF-{tag}-1 attached"));
            Assert.Contains(a, x => x!.Contains("changed from No to Yes"));
            Assert.Contains(a, x => x!.Contains("changed from Yes to No"));
        }
    }
}
