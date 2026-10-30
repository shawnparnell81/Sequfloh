using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>Qty completed shows on the record, the work order list and the full record print for every work order type. Made-up data only.</summary>
[Collection("api")]
public sealed class QtyCompletedTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";

    [Fact]
    public async Task Qty_completed_is_on_the_record_the_list_and_the_full_record_print_for_every_type()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync([Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohStartTask, Permissions.SequflohCompleteTask,
            Permissions.SequflohViewReports, Permissions.SequflohViewLiveExecution], site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", quantity = 25, minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await c.StartAsync(id);
        await c.PostOkAsync($"{T}/{id}/job/quantity", new { completed = 7 });

        foreach (var type in Enum.GetValues<TrackingType>())
        {
            using (var s = app.Services.CreateScope())
            {
                var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.SequflohTrackingRecords.IgnoreQueryFilters().Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Type, type));
            }
            var rec = (await c.GetElAsync($"{T}/{id}")).GetProperty("record");
            Assert.Equal(7, rec.GetProperty("quantityCompleted").GetInt32());
            Assert.Equal(25, rec.GetProperty("quantityTarget").GetInt32());
            var pdf = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/print")).Content.ReadAsByteArrayAsync());
            Assert.True(pdf.Contains("Qty completed") && pdf.Contains("7 of 25"), $"{type}: Qty completed missing from the full record print");
        }
        var no = (await c.GetElAsync($"{T}/{id}")).GetProperty("record").Str("number");
        var row = (await c.GetElAsync($"{T}?q={no}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(7, row.GetProperty("quantityCompleted").GetInt32());
        Assert.Equal(25, row.GetProperty("quantityTarget").GetInt32());
    }
}
