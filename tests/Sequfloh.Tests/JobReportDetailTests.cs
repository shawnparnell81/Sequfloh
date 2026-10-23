using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class JobReportDetailTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohStartTask,
        Permissions.SequflohCompleteTask, Permissions.SequflohEditParts, Permissions.SequflohViewAuditTrail];

    async Task<(Guid Id, ApiClient Planner, ApiClient Plain)> JobAsync()
    {
        var site = await app.CreateSiteAsync();
        var (plannerId, planner) = await app.CreateUserAsync(Planner, site);
        var (_, plain) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        Guid wi;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var w = new SequflohWorkInstruction { Type = WorkInstructionType.Validation, Title = "Made-up repair", Number = "DWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "C", SiteId = site,
                Status = WorkInstructionStatus.Released };
            w.FamilyId = w.Id;
            db.SequflohWorkInstructions.Add(w);
            await db.SaveChangesAsync();
            wi = w.Id;
        }
        var id = (await (await planner.PostOkAsync(U, new { type = "Production", workInstructionId = wi, siteId = site })).JsonAsync()).Id();
        var v = (await planner.GetElAsync($"{U}/{id}")).GetProperty("record").GetProperty("stateVersion").GetInt32();
        await planner.PostOkAsync($"{U}/{id}/actions/dispatch", new { operationId = Guid.NewGuid(), stateVersion = v, assignedUserId = plannerId });
        return (id, planner, plain);
    }

    static async Task<string> PdfText(ApiClient c, string url) => Encoding.Latin1.GetString(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync());

    [Fact]
    public async Task Paused_time_is_logged_per_technician_and_left_out_of_man_hours()
    {
        var (id, _, plain) = await JobAsync();
        await plain.PostOkAsync($"{U}/{id}/time/start");
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PostAsync($"{U}/{id}/time/pause", new { reason = "Payroll" })).StatusCode);
        await plain.PostOkAsync($"{U}/{id}/time/pause", new { reason = "Lunch" });
        Assert.Equal("Lunch", (await plain.GetElAsync($"{U}/{id}/time")).GetProperty("minePauseReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await plain.PostAsync($"{U}/{id}/time/pause", new { reason = "Break" })).StatusCode);
        // move the clock back: started 3 h ago, paused for the last 1 h
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var e = await db.SequflohTimeEntries.SingleAsync(x => x.TrackingRecordId == id);
            e.StartedAt = e.StartedAt.AddHours(-3);
            e.PausedAt = e.PausedAt!.Value.AddHours(-1);
            await db.SaveChangesAsync();
        }
        await plain.PostOkAsync($"{U}/{id}/time/resume");
        await plain.PostOkAsync($"{U}/{id}/time/stop");
        var time = await plain.GetElAsync($"{U}/{id}/time");
        var person = time.GetProperty("people")[0];
        Assert.InRange(person.GetProperty("seconds").GetInt64(), 7190, 7260);
        Assert.InRange(person.GetProperty("pausedSeconds").GetInt64(), 3590, 3660);
        var kinds = time.GetProperty("log").EnumerateArray().Select(l => l.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["Start", "Pause", "Resume", "Stop"], kinds);
        var labels = time.GetProperty("log").EnumerateArray().Select(l => l.GetProperty("label").GetString()).ToList();
        Assert.Equal(["Clocked on job", "Paused for lunch", "Clocked back on job", "Clocked off job"], labels);
        var report = await plain.GetElAsync($"{U}/{id}/job/report");
        Assert.InRange(report.GetProperty("totalSeconds").GetInt64(), 7190, 7260);
        Assert.Equal(1, report.GetProperty("people")[0].GetProperty("sessions").GetInt32());
        Assert.Equal("Paused for lunch", report.GetProperty("timeLog")[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Report_sections_are_optional_remembered_and_empty_ones_are_left_out()
    {
        var (id, _, plain) = await JobAsync();
        var job = await plain.GetElAsync($"{U}/{id}/job");
        Assert.False(job.GetProperty("reportOptions").GetProperty("findings").GetBoolean()); // summary only by default
        var summary = await PdfText(plain, $"{U}/{id}/job/report/pdf");
        Assert.DoesNotContain("Findings / nonconformance", summary);
        Assert.DoesNotContain("Official status", summary);
        // everything ticked but nothing entered: empty sections stay out
        await plain.PutAsync($"{U}/{id}/job/report-options", new { perTech = true, timeLog = true, findings = true, tests = true, attachments = true, photos = true, cost = true });
        Assert.True((await plain.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("timeLog").GetBoolean());
        var empty = await PdfText(plain, $"{U}/{id}/job/report/pdf");
        foreach (var s in new[] { "Findings / nonconformance", "Detailed time log", "Man hours by technician", "Test results", "Attachments", "Photos and evidence" })
            Assert.DoesNotContain(s, empty);
        Assert.Contains("Chargeback summary", empty);
        await plain.PutAsync($"{U}/{id}/job/findings", new { description = "Made-up cracked housings", qtyInspected = 50 });
        Assert.Contains("Findings / nonconformance", await PdfText(plain, $"{U}/{id}/job/report/pdf"));
        await plain.PutAsync($"{U}/{id}/job/report-options", new { });
        Assert.DoesNotContain("Findings / nonconformance", await PdfText(plain, $"{U}/{id}/job/report/pdf"));
        Assert.Contains("Findings / nonconformance", await PdfText(plain, $"{U}/{id}/job/report/pdf?full=true"));
    }

    /// <summary>The words a reader sees in the PDF: the text shown by each Tj / TJ operator.</summary>
    static string Shown(string pdf) => string.Join(" ", System.Text.RegularExpressions.Regex.Matches(pdf, @"\(((?:\\.|[^\\)])*)\)\s*Tj|\[(.*?)\]\s*TJ").Select(m => m.Value));

    [Fact]
    public async Task Test_results_are_left_out_completely_when_the_answer_is_no_and_the_erp_reference_is_never_printed()
    {
        var (id, _, plain) = await JobAsync();
        var csv = "Serial,Result,Volts\nA1,PASS,12.1\nA2,FAIL,11.2\n"u8.ToArray();
        var form = new MultipartFormDataContent { { new ByteArrayContent(csv), "files", "tester-run.csv" }, { new StringContent("2026-10-20"), "testDate" } };
        Assert.True((await plain.Http.PostAsync($"{U}/{id}/job/test-files", form)).IsSuccessStatusCode);
        await plain.PutAsync($"{U}/{id}/job/findings", new { description = "Made-up cracked housings", erpReference = "ERP-998877" });
        // default: No
        Assert.False((await plain.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("tests").GetBoolean());
        foreach (var url in new[] { $"{U}/{id}/job/report/pdf", $"{U}/{id}/job/report/pdf?full=true" })
        {
            // the test fixture's own organisation and user names are not report wording
            var text = System.Text.RegularExpressions.Regex.Replace(Shown(await PdfText(plain, url)), @"Test Organization|Test u[0-9a-f]+", "");
            Assert.NotEmpty(text);
            Assert.True(System.Text.RegularExpressions.Regex.Matches(text, "test", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count == 0, text);
            Assert.DoesNotContain("Pass rate", text);
            Assert.DoesNotContain("ERP", text);
            Assert.DoesNotContain("998877", text);
        }
        var noJson = await plain.GetElAsync($"{U}/{id}/job/report");
        Assert.Empty(noJson.GetProperty("files").EnumerateArray());
        Assert.Equal(0, noJson.GetProperty("tested").GetInt32());
        // Yes: the section and the file list come back, remembered for the work order; the ERP reference still stays off
        await plain.PutAsync($"{U}/{id}/job/report-options", new { findings = true, tests = true });
        Assert.True((await plain.GetElAsync($"{U}/{id}/job")).GetProperty("reportOptions").GetProperty("tests").GetBoolean());
        var yes = Shown(await PdfText(plain, $"{U}/{id}/job/report/pdf?full=true"));
        Assert.Contains("Test results", yes);
        Assert.Contains("tester-run.csv", yes);
        Assert.Contains("Findings / nonconformance", yes);
        Assert.DoesNotContain("998877", yes);
        // the ERP reference is kept on the work order's findings
        Assert.Equal("ERP-998877", (await plain.GetElAsync($"{U}/{id}/job")).GetProperty("findings").GetProperty("erpReference").GetString());
    }

    [Fact]
    public async Task Findings_are_validated_and_audited_and_costs_add_up()
    {
        var (id, _, plain) = await JobAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PutAsync($"{U}/{id}/job/findings", new { discoveryMethod = "Made up" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.PutAsync($"{U}/{id}/job/findings", new { qtyAffected = -1 })).StatusCode);
        Assert.True((await plain.PutAsync($"{U}/{id}/job/findings", new { discoveryMethod = "AQL sampling inspection", supplier = "Made-up supplier", qtyNonconforming = 12, repairMethod = "Replace top mount" })).IsSuccessStatusCode);
        await plain.PostOkAsync($"{U}/{id}/job/quantity", new { completed = 10 });
        await plain.PutAsync($"{U}/{id}/job/parts", new[] { new { partNumber = "123abc", perAssembly = 1m, unitCost = (decimal?)2.5m } });
        await plain.PutAsync($"{U}/{id}/job/cost-lines", new[] { new { description = "Freight", amount = 40m }, new { description = " ", amount = 9m } });
        var report = await plain.GetElAsync($"{U}/{id}/job/report");
        Assert.Equal(25m, report.GetProperty("partsCost").GetDecimal());
        Assert.Equal(40m, report.GetProperty("otherCost").GetDecimal());
        Assert.Equal("AQL sampling inspection", report.GetProperty("findings").GetProperty("discoveryMethod").GetString());
        Assert.Equal("C", report.GetProperty("workInstructionRevision").GetString());
        var pdf = await PdfText(plain, $"{U}/{id}/job/report/pdf?full=true&rate=50");
        Assert.Contains("65.00", pdf); // no hours logged: 0 labor + 25 parts + 40 other
        var history = (await plain.GetElAsync($"{U}/{id}/history")).EnumerateArray().Select(h => h.GetProperty("description").GetString()).ToList();
        Assert.Contains(history, h => h!.Contains("Findings on"));
        Assert.Contains(history, h => h!.Contains("Freight 40.00"));
    }

    [Fact]
    public async Task Evidence_photos_and_attachments_are_kept_apart_from_test_results()
    {
        var (id, _, plain) = await JobAsync();
        async Task<HttpResponseMessage> Send(string name, string kind)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]), "files", name }, { new StringContent(kind), "kind" } };
            return await plain.Http.PostAsync($"{U}/{id}/job/test-files", form);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("notes.csv", "evidence")).StatusCode);
        Assert.True((await Send("crack.jpg", "evidence")).IsSuccessStatusCode);
        Assert.True((await Send("claim.jpg", "attachment")).IsSuccessStatusCode);
        var report = await plain.GetElAsync($"{U}/{id}/job/report");
        Assert.Single(report.GetProperty("photos").EnumerateArray());
        Assert.Single(report.GetProperty("attachments").EnumerateArray());
        Assert.Empty(report.GetProperty("files").EnumerateArray());
        Assert.Equal(0, report.GetProperty("tested").GetInt32());
        await plain.PutAsync($"{U}/{id}/job/report-options", new { photos = true, attachments = true });
        var pdf = await PdfText(plain, $"{U}/{id}/job/report/pdf");
        Assert.Contains("Photos and evidence", pdf);
        Assert.Contains("claim.jpg", pdf);
    }
}
