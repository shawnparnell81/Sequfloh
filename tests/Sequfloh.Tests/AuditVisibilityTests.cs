using System.Net;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Audit trails are read only with the audit trail permissions: the full trail on the Administration page, the SEQUFLOH views for people given that permission. Synthetic data only.</summary>
[Collection("api")]
public sealed class AuditVisibilityTests(TestApp app)
{
    [Fact]
    public async Task Without_the_permission_every_audit_and_record_history_read_is_refused()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction], site);
        var wi = (await (await author.PostOkAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Example audit check", siteId = site })).JsonAsync()).Id();
        foreach (var url in new[] { "/api/sequfloh/audit/system", "/api/sequfloh/audit/system/csv", $"/api/sequfloh/instructions/{wi}/history", "/api/audit-trail", "/api/audit-trail/filters" })
            Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task With_the_SEQUFLOH_audit_permission_the_views_open_but_the_full_trail_stays_on_the_admin_page()
    {
        var site = await app.CreateSiteAsync();
        var (_, author) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateWorkInstruction, Permissions.SequflohViewAuditTrail], site);
        var wi = (await (await author.PostOkAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Example audit check", siteId = site })).JsonAsync()).Id();
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/api/sequfloh/audit/instructions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync($"/api/sequfloh/instructions/{wi}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.GetAsync("/api/audit-trail")).StatusCode);
    }

    [Fact]
    public async Task The_admin_trail_filters_by_record_type_and_record()
    {
        var site = await app.CreateSiteAsync();
        var admin = await app.AdminAsync();
        var wi = (await (await admin.PostOkAsync("/api/sequfloh/instructions", new { type = "Validation", title = "Example admin audit", siteId = site })).JsonAsync()).Id();
        var r = await admin.GetElAsync($"/api/audit-trail?entityType=SequflohWorkInstruction&entityId={wi}");
        Assert.True(r.Int("total") >= 1);
        Assert.All(r.GetProperty("items").EnumerateArray(), x => Assert.Equal(wi, x.Id("entityId")));
        var f = await admin.GetElAsync("/api/audit-trail/filters");
        Assert.Contains(f.GetProperty("types").EnumerateArray(), t => t.GetString() == "SequflohWorkInstruction");
    }
}
