using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohDashboardTests(TestApp app)
{
    const string S = "/api/sequfloh";
    static readonly string[] Eng = [Permissions.SequflohAccess, Permissions.SequflohViewEngineeringFolder, Permissions.SequflohEditEngineeringFolder];
    static readonly string[] Qual = [Permissions.SequflohAccess, Permissions.SequflohViewQualityFolder, Permissions.SequflohViewDashboard];

    async Task<(Guid Id, string Number)> WiAsync(Guid site, WorkInstructionStatus status, WorkInstructionType type = WorkInstructionType.Validation)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = type, Title = "Bracket check", Number = "DWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site, Status = status,
            Steps = "[]", Characteristics = "[]", PartsRows = "[]", Content = "{}" };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return (wi.Id, wi.Number);
    }

    [Fact]
    public async Task Dashboards_and_review_queues_follow_permissions_and_status()
    {
        var site = await app.CreateSiteAsync();
        var (_, eng) = await app.CreateUserAsync(Eng, site);
        var (_, qual) = await app.CreateUserAsync(Qual, site);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var draft = await WiAsync(site, WorkInstructionStatus.Draft);
        var review = await WiAsync(site, WorkInstructionStatus.PendingReview);
        await WiAsync(site, WorkInstructionStatus.Released, WorkInstructionType.RepairWork);

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"{S}/quality/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"{S}/engineering/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await qual.GetAsync($"{S}/engineering/review-queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.GetAsync($"{S}/quality/review-queue")).StatusCode);

        var qd = await qual.GetElAsync($"{S}/quality/dashboard");
        Assert.True(qd.GetProperty("validationPendingReview").GetInt32() >= 1);
        var ed = await eng.GetElAsync($"{S}/engineering/dashboard");
        Assert.True(ed.GetProperty("draftValidation").GetInt32() >= 1);
        Assert.True(ed.GetProperty("releasedValidationInstructions").GetInt32() >= 0);

        var queue = (await qual.GetElAsync($"{S}/quality/review-queue?q={review.Number}")).EnumerateArray().ToList();
        Assert.Single(queue);
        Assert.Equal("Work instruction", queue[0].Str("kind"));
        Assert.Equal($"/sequfloh/instruction/{review.Id}", queue[0].Str("path"));
        Assert.Empty((await qual.GetElAsync($"{S}/quality/review-queue?q={draft.Number}")).EnumerateArray());

        // the home dashboard leaves out live execution for someone without that permission
        var home = await qual.GetElAsync($"{S}/dashboard");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, home.GetProperty("live").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync($"{S}/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Development_details_are_saved_audited_and_limited_to_unreleased_instructions()
    {
        var site = await app.CreateSiteAsync();
        var (engId, eng) = await app.CreateUserAsync(Eng, site);
        var (viewerId, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohViewEngineeringFolder], site);
        var draft = await WiAsync(site, WorkInstructionStatus.Draft);
        var released = await WiAsync(site, WorkInstructionStatus.Released);
        var body = new { assignedEngineerId = engId, targetCompletionDate = "2026-12-01", project = "Bracket redesign", engineeringNotes = "Torque values pending test data.", developmentStatus = "Drafting steps" };

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync($"{S}/instructions/{draft.Id}/development", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await eng.PutAsync($"{S}/instructions/{released.Id}/development", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PutAsync($"{S}/instructions/{draft.Id}/development", new { assignedEngineerId = viewerId })).StatusCode);
        var ok = await eng.PutAsync($"{S}/instructions/{draft.Id}/development", body);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        // an empty instruction cannot be recommended for release
        Assert.Equal(HttpStatusCode.BadRequest, (await eng.PutAsync($"{S}/instructions/{draft.Id}/development", new { recommendRelease = true })).StatusCode);

        var doc = (await eng.GetElAsync($"{S}/instructions/{draft.Id}")).GetProperty("instruction");
        Assert.Equal("Bracket redesign", doc.Str("project"));
        Assert.Equal("Drafting steps", doc.Str("developmentStatus"));
        Assert.Equal("2026-12-01", doc.Str("targetCompletionDate"));

        var mine = (await eng.GetElAsync($"{S}/engineering/dashboard")).GetProperty("myDevelopment").EnumerateArray();
        Assert.Contains(mine, x => x.Str("number") == draft.Number);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(db.AuditEntries, a => a.EntityId == draft.Id && a.Action == "DevelopmentUpdated");
    }
}
