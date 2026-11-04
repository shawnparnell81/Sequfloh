using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Messages between people: one-to-one, groups, unread, site scope, edit and delete. Made-up data only.</summary>
[Collection("api")]
public sealed class MessageTests(TestApp app)
{
    static readonly string[] Use = [Permissions.MessagesUse];

    [Fact]
    public async Task Send_reply_unread_and_a_group_stay_with_the_people_in_the_thread()
    {
        var admin = await app.AdminAsync();
        var grants = (await admin.GetElAsync("/api/auth/session")).GetProperty("user").GetProperty("grants").EnumerateArray().Select(g => g.Str("permission")).ToList();
        Assert.Contains(Permissions.MessagesUse, grants);
        Assert.DoesNotContain(Permissions.MessagesAdmin, grants);

        var (aId, a) = await app.CreateUserAsync(Use);
        var (bId, b) = await app.CreateUserAsync(Use);
        var (cId, c) = await app.CreateUserAsync(Use);
        var (_, outsider) = await app.CreateUserAsync(Use);
        var (_, denied) = await app.CreateUserAsync([Permissions.SequflohAccess]);

        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/messages/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.PostAsync("/api/messages/conversations", new { recipientIds = new[] { aId }, body = "no" })).StatusCode);

        Assert.Equal(0, (await b.GetElAsync("/api/messages/unread-count")).Int("count"));
        var started = await (await a.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { bId }, body = "hello from A" })).JsonAsync();
        var direct = started.Id();
        Assert.False(started.GetProperty("isGroup").GetBoolean());

        Assert.Equal(1, (await b.GetElAsync("/api/messages/unread-count")).Int("count"));
        var listed = await b.GetElAsync("/api/messages/conversations");
        var row = listed.EnumerateArray().Single(x => x.Id() == direct);
        Assert.True(row.GetProperty("unread").GetBoolean());
        Assert.Contains("hello from A", row.Str("preview"));

        var thread = await b.GetElAsync($"/api/messages/conversations/{direct}");
        var first = thread.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("hello from A", first.Str("body"));
        Assert.False(string.IsNullOrWhiteSpace(first.Str("senderName")));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("createdAt").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/messages/conversations/{direct}")).StatusCode);

        await b.PostAsync($"/api/messages/conversations/{direct}/read");
        Assert.Equal(0, (await b.GetElAsync("/api/messages/unread-count")).Int("count"));
        Assert.False((await b.GetElAsync("/api/messages/conversations")).EnumerateArray().Single(x => x.Id() == direct).GetProperty("unread").GetBoolean());

        await b.PostOkAsync($"/api/messages/conversations/{direct}/messages", new { body = "hello back" });
        var back = (await a.GetElAsync($"/api/messages/conversations/{direct}")).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["hello from A", "hello back"], back.Select(m => m.Str("body")).ToArray());

        // the same two people keep one thread
        var again = await (await a.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { bId }, body = "still here" })).JsonAsync();
        Assert.Equal(direct, again.Id());

        await a.PutOkAsync($"/api/messages/messages/{started.GetProperty("messageId").GetGuid()}", new { body = "hello edited" });
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PutAsync($"/api/messages/messages/{started.GetProperty("messageId").GetGuid()}", new { body = "not yours" })).StatusCode);
        var edited = (await b.GetElAsync($"/api/messages/conversations/{direct}")).GetProperty("messages").EnumerateArray().First();
        Assert.Equal("hello edited", edited.Str("body"));
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);

        await a.DeleteAsync($"/api/messages/messages/{started.GetProperty("messageId").GetGuid()}");
        var gone = (await b.GetElAsync($"/api/messages/conversations/{direct}")).GetProperty("messages").EnumerateArray().First();
        Assert.True(gone.GetProperty("deleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("body").ValueKind);

        var audit = await admin.GetElAsync($"/api/audit-trail?entityType=Message&entityId={started.GetProperty("messageId").GetGuid()}&pageSize=20");
        var items = audit.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.Str("description").Contains("Edited their message") && i.Str("description").Contains("hello edited"));
        Assert.Contains(items, i => i.Str("description").Contains("Deleted their message") && i.Str("description").Contains("hello edited"));
        Assert.All(items, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Str("who")));
            Assert.False(string.IsNullOrWhiteSpace(i.Str("what")));
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("when").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(i.Str("description")));
        });

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsync("/api/messages/conversations", new { recipientIds = new[] { bId, cId }, body = "no name" })).StatusCode);
        var group = await (await a.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { bId, cId }, title = "Floor notes", body = "stand up" })).JsonAsync();
        Assert.True(group.GetProperty("isGroup").GetBoolean());
        var seen = await c.GetElAsync("/api/messages/conversations");
        var grow = seen.EnumerateArray().Single(x => x.Id() == group.Id());
        Assert.Equal("Floor notes", grow.Str("heading"));
        Assert.True(grow.GetProperty("unread").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/messages/conversations/{group.Id()}")).StatusCode);
        await c.PostOkAsync($"/api/messages/conversations/{group.Id()}/messages", new { body = "on my way" });
        var names = (await b.GetElAsync($"/api/messages/conversations/{group.Id()}")).GetProperty("messages").EnumerateArray().Select(m => m.Str("senderName")).ToList();
        Assert.Equal(2, names.Distinct().Count());
    }

    [Fact]
    public async Task A_main_site_role_can_message_anyone_and_other_sites_stay_put()
    {
        var admin = await app.AdminAsync();
        var main = await app.CreateSiteAsync();
        var other = await app.CreateSiteAsync();
        var plain = await app.CreateSiteAsync();
        await admin.PutOkAsync($"/api/organization/sites/{main}/main", new { isMainSite = true });
        var dept = await (await admin.PostOkAsync($"/api/organization/sites/{main}/departments", new { code = "M1", name = "Made-up dept" })).JsonAsync();

        var (plantId, plant) = await app.CreateUserAsync(Use, other);
        var (_, home) = await app.CreateUserAsync(Use, plain);
        var (_, mainUser) = await app.CreateUserAsync(Use, main);
        var (_, deptUser) = await app.CreateUserAsync(Use, main, dept.GetGuid());

        Assert.Contains((await mainUser.GetElAsync("/api/messages/people")).EnumerateArray(), p => p.Id() == plantId);
        Assert.DoesNotContain((await home.GetElAsync("/api/messages/people")).EnumerateArray(), p => p.Id() == plantId);
        Assert.DoesNotContain((await deptUser.GetElAsync("/api/messages/people")).EnumerateArray(), p => p.Id() == plantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await home.PostAsync("/api/messages/conversations", new { recipientIds = new[] { plantId }, body = "across sites" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await deptUser.PostAsync("/api/messages/conversations", new { recipientIds = new[] { plantId }, body = "from a department" })).StatusCode);

        var started = await (await mainUser.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { plantId }, body = "from the main site" })).JsonAsync();
        Assert.Equal("from the main site", (await plant.GetElAsync($"/api/messages/conversations/{started.Id()}")).GetProperty("messages").EnumerateArray().Single().Str("body"));
        await plant.PostOkAsync($"/api/messages/conversations/{started.Id()}/messages", new { body = "got it" });
        Assert.Contains((await mainUser.GetElAsync($"/api/messages/conversations/{started.Id()}")).GetProperty("messages").EnumerateArray(), m => m.Str("body") == "got it");
    }

    [Fact]
    public async Task Compliance_review_reads_conversations_in_scope_and_a_link_must_be_one_you_can_open()
    {
        var admin = await app.AdminAsync();
        var site = await app.CreateSiteAsync();
        var elsewhere = await app.CreateSiteAsync();
        var (aId, a) = await app.CreateUserAsync(Use, site);
        var (bId, _) = await app.CreateUserAsync(Use, site);
        var (_, far) = await app.CreateUserAsync(Use, elsewhere);
        var started = await (await a.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { bId }, body = "keep this" })).JsonAsync();
        var messageId = started.GetProperty("messageId").GetGuid();
        await a.DeleteAsync($"/api/messages/messages/{messageId}");

        var (_, reviewer) = await app.CreateUserAsync([Permissions.MessagesAdmin], site);
        var (_, otherReviewer) = await app.CreateUserAsync([Permissions.MessagesAdmin], elsewhere);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync("/api/messages/conversations")).StatusCode);
        var all = await reviewer.GetElAsync("/api/messages/admin/conversations");
        Assert.Contains(all.EnumerateArray(), c => c.Id() == started.Id());
        Assert.DoesNotContain((await otherReviewer.GetElAsync("/api/messages/admin/conversations")).EnumerateArray(), c => c.Id() == started.Id());
        var revealed = (await reviewer.GetElAsync($"/api/messages/admin/conversations/{started.Id()}")).GetProperty("messages").EnumerateArray().Single();
        Assert.True(revealed.GetProperty("deleted").GetBoolean());
        Assert.Equal("keep this", revealed.Str("body"));
        Assert.Equal(HttpStatusCode.NotFound, (await otherReviewer.GetAsync($"/api/messages/admin/conversations/{started.Id()}")).StatusCode);

        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", new
        {
            part = new { partNumber = "MU-MSG-" + Guid.NewGuid().ToString("N")[..8], name = "Made-up part", description = "", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
            typeId = (Guid?)null, attributes = (object?)null,
        })).JsonAsync()).Id();
        var wo = (await (await admin.PostOkAsync("/api/sequfloh/tracking", new { type = "Rebox", siteId = site, partId = part, number = "MU-MSG-" + Guid.NewGuid().ToString("N")[..8] })).JsonAsync()).Id();
        var linked = await (await admin.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { aId }, body = "see the order", linkKind = "work-order", linkId = wo })).JsonAsync();
        var shown = (await a.GetElAsync($"/api/messages/conversations/{linked.Id()}")).GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("work-order", shown.Str("linkKind"));
        Assert.Equal($"/sequfloh/record/{wo}", shown.Str("linkPath"));
        Assert.False(string.IsNullOrWhiteSpace(shown.Str("linkLabel")));

        Assert.Equal(HttpStatusCode.BadRequest, (await far.PostAsync("/api/messages/conversations", new { recipientIds = new[] { bId }, body = "across sites" })).StatusCode);
        var (_, bare) = await app.CreateUserAsync(Use);
        Assert.Equal(HttpStatusCode.BadRequest, (await bare.PostAsync("/api/messages/conversations", new { recipientIds = new[] { aId }, body = "no access", linkKind = "work-order", linkId = wo })).StatusCode);
        var audit = await admin.GetElAsync($"/api/audit-trail?entityType=Message&entityId={linked.GetProperty("messageId").GetGuid()}");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), i => i.Str("description").Contains("Linked work order"));
    }
}
