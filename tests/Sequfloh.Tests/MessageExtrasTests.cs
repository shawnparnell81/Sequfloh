using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Files, reactions, work-order cards and presence on messages. Made-up data only.</summary>
[Collection("api")]
public sealed class MessageExtrasTests(TestApp app)
{
    static readonly string[] Use = [Permissions.MessagesUse];
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Attachments_reactions_a_work_order_card_and_presence()
    {
        var admin = await app.AdminAsync();
        var (friendId, friend) = await app.CreateUserAsync(Use);
        var (_, outsider) = await app.CreateUserAsync(Use);
        var (_, reviewer) = await app.CreateUserAsync([Permissions.MessagesAdmin]);
        var me = await app.AdminAsync();

        var started = await (await me.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { friendId }, body = "hello" })).JsonAsync();
        var id = started.Id();

        var png = new ByteArrayContent(Png);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var note = new ByteArrayContent("hello file"u8.ToArray());
        note.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var form = new MultipartFormDataContent { { new StringContent("see these"), "body" } };
        form.Add(png, "files", "tiny.png");
        form.Add(note, "files", "notes.txt");
        var sent = await me.Http.PostAsync($"/api/messages/conversations/{id}/messages/with-files", form);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());

        var tooBig = new MultipartFormDataContent { { new StringContent("nope"), "body" }, { new ByteArrayContent(new byte[11 * 1024 * 1024]), "files", "big.png" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await me.Http.PostAsync($"/api/messages/conversations/{id}/messages/with-files", tooBig)).StatusCode);
        var badType = new MultipartFormDataContent { { new StringContent("nope"), "body" }, { new ByteArrayContent([1, 2, 3]), "files", "run.exe" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await me.Http.PostAsync($"/api/messages/conversations/{id}/messages/with-files", badType)).StatusCode);

        var thread = await friend.GetElAsync($"/api/messages/conversations/{id}");
        var withFiles = thread.GetProperty("messages").EnumerateArray().Single(m => m.Str("body") == "see these");
        var attachments = withFiles.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(2, attachments.Count);
        Assert.Contains(attachments, f => f.Str("fileName") == "tiny.png" && f.GetProperty("image").GetBoolean());
        var text = attachments.Single(f => f.Str("fileName") == "notes.txt");
        Assert.False(text.GetProperty("image").GetBoolean());

        var picture = await friend.GetAsync($"/api/messages/files/{attachments.Single(f => f.Str("fileName") == "tiny.png").Id()}");
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await picture.Content.ReadAsByteArrayAsync());
        var saved = await friend.GetAsync($"/api/messages/files/{text.Id()}");
        Assert.Equal("hello file", await saved.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/messages/files/{text.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.GetAsync($"/api/messages/files/{text.Id()}")).StatusCode);

        var audit = await admin.GetElAsync($"/api/audit-trail?entityType=Message&entityId={withFiles.Id()}");
        var described = audit.GetProperty("items").EnumerateArray().Select(i => i.Str("description")).ToList();
        Assert.Contains(described, d => d.Contains("tiny.png") && d.Contains("notes.txt") && d.Contains("Attached"));
        Assert.All(audit.GetProperty("items").EnumerateArray(), i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Str("who")));
            Assert.False(string.IsNullOrWhiteSpace(i.Str("what")));
            Assert.False(string.IsNullOrWhiteSpace(i.GetProperty("when").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(i.Str("description")));
        });

        Assert.Equal(HttpStatusCode.NoContent, (await friend.PostAsync($"/api/messages/messages/{withFiles.Id()}/reactions", new { emoji = "\uD83D\uDC4D" })).StatusCode);
        await me.PostAsync($"/api/messages/messages/{withFiles.Id()}/reactions", new { emoji = "\uD83D\uDC4D" });
        var reacted = (await friend.GetElAsync($"/api/messages/conversations/{id}")).GetProperty("messages").EnumerateArray().Single(m => m.Id() == withFiles.Id());
        var thumb = reacted.GetProperty("reactions").EnumerateArray().Single();
        Assert.Equal("\uD83D\uDC4D", thumb.Str("emoji"));
        Assert.Equal(2, thumb.Int("count"));
        Assert.True(thumb.GetProperty("mine").GetBoolean());
        Assert.Equal(2, thumb.GetProperty("names").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await friend.PostAsync($"/api/messages/messages/{withFiles.Id()}/reactions", new { emoji = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/api/messages/messages/{withFiles.Id()}/reactions", new { emoji = "\uD83D\uDC4D" })).StatusCode);
        await friend.PostAsync($"/api/messages/messages/{withFiles.Id()}/reactions", new { emoji = "\uD83D\uDC4D" });
        var after = (await me.GetElAsync($"/api/messages/conversations/{id}")).GetProperty("messages").EnumerateArray().Single(m => m.Id() == withFiles.Id());
        Assert.Equal(1, after.GetProperty("reactions").EnumerateArray().Single().Int("count"));
        Assert.True(after.GetProperty("reactions").EnumerateArray().Single().GetProperty("mine").GetBoolean());

        var site = (await admin.GetElAsync("/api/organization/sites")).EnumerateArray().First().Id();
        var part = (await (await admin.PostOkAsync("/api/sequfloh/parts", new
        {
            part = new { partNumber = "MU-CARD-" + Guid.NewGuid().ToString("N")[..8], name = "Made-up bracket", description = "bracket", partType = 0, revision = "A", unitOfMeasure = "EA", productId = (Guid?)null, material = "",
                hasSpecialCharacteristics = false, siteId = site, departmentId = (Guid?)null, status = 0, rowVersion = 0u },
            execution = new { productFamily = (string?)null, assemblyType = (string?)null, isRepairable = false, isSerialized = false, isLotControlled = false, drawingNumber = (string?)null, specification = (string?)null },
            typeId = (Guid?)null, attributes = (object?)null,
        })).JsonAsync()).Id();
        var wo = await (await admin.PostOkAsync("/api/sequfloh/tracking", new { type = "Rebox", siteId = site, partId = part, number = "MU-CARD-" + Guid.NewGuid().ToString("N")[..8] })).JsonAsync();
        await admin.PutOkAsync($"/api/sequfloh/tracking/{wo.Id()}/job/findings", new { supplier = "Made-up Metals", hasFindings = true });
        var linked = await (await me.PostOkAsync("/api/messages/conversations", new { recipientIds = new[] { friendId }, body = "the order", linkKind = "work-order", linkId = wo.Id() })).JsonAsync();
        var cardMsg = (await friend.GetElAsync($"/api/messages/conversations/{linked.Id()}")).GetProperty("messages").EnumerateArray().Single(m => m.Str("body") == "the order");
        var card = cardMsg.GetProperty("linkCard");
        Assert.Equal("work-order", card.Str("kind"));
        Assert.Equal(wo.Str("number"), card.Str("number"));
        Assert.Contains("MU-CARD-", card.Str("part"));
        Assert.False(string.IsNullOrWhiteSpace(card.Str("status")));
        Assert.Equal("Made-up Metals", card.Str("supplier"));
        Assert.Equal($"/sequfloh/record/{wo.Id()}", card.Str("path"));

        Assert.Equal(HttpStatusCode.NoContent, (await friend.PostAsync("/api/presence/heartbeat")).StatusCode);
        var online = (await me.GetElAsync($"/api/presence?ids={friendId}")).EnumerateArray().Single();
        Assert.True(online.GetProperty("online").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(online.GetProperty("lastSeenAt").GetString()));

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == friendId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, DateTimeOffset.UtcNow.AddMinutes(-10)));
        }
        var away = (await me.GetElAsync($"/api/presence?ids={friendId}")).EnumerateArray().Single();
        Assert.False(away.GetProperty("online").GetBoolean());
        var nobody = await me.GetElAsync($"/api/presence?ids={Guid.NewGuid()}");
        Assert.Equal(0, nobody.GetArrayLength());
    }
}
