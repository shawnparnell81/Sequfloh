using System.Net;
using System.Text;
using System.Text.Json;
using Sequfloh.Domain.Security;

namespace Sequfloh.Tests;

/// <summary>Day worker and crew time on the job report, and the per-worker overlap rule for entered time. Made-up data only.</summary>
[Collection("api")]
public sealed class DayWorkerReportTests(TestApp app)
{
    const string T = "/api/sequfloh/tracking";
    static readonly string[] Lead = [Permissions.SequflohAccess, Permissions.SequflohCreateProductionTask, Permissions.SequflohManageCrewTime, Permissions.SequflohViewReports, Permissions.SequflohDispatchTask];

    async Task<(Guid Id, ApiClient C)> InspectionAsync()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Lead, site);
        var id = (await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync()).Id();
        await c.StartAsync(id);
        return (id, c);
    }

    static async Task<Guid> WorkerAsync(ApiClient c, Guid id, string name) =>
        (await (await c.PostOkAsync($"{T}/{id}/crew/workers", new { name })).JsonAsync()).GetProperty("workers").EnumerateArray().First(w => w.Str("name") == name).Id();

    [Fact]
    public async Task Day_worker_and_crew_time_are_on_the_job_report_and_pdf_of_an_inspection_work_order()
    {
        var (id, c) = await InspectionAsync();
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1).Date.AddHours(13), TimeSpan.Zero);
        var w = await WorkerAsync(c, id, "Made-up Dana");
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, workerId = w, start, stop = start.AddHours(3) });
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 2, start, stop = start.AddHours(1) });

        var rep = await c.GetElAsync($"{T}/{id}/job/report");
        var people = rep.GetProperty("people").EnumerateArray().ToDictionary(p => p.Str("name")!, p => p.GetProperty("seconds").GetInt64());
        Assert.Equal(3 * 3600, people["Made-up Dana (day worker)"]);
        Assert.Equal(2 * 3600, people["Crew (unnamed workers)"]);
        Assert.Equal(5 * 3600, rep.GetProperty("totalSeconds").GetInt64());
        Assert.Contains(rep.GetProperty("timeLog").EnumerateArray(), e => e.Str("name") == "Made-up Dana (day worker)");

        var pdf = Encoding.Latin1.GetString(await (await c.GetAsync($"{T}/{id}/job/report/pdf?full=true")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Made-up Dana", pdf); Assert.Contains("Man hours by technician", pdf);
        Assert.Contains("5.00", pdf);
    }

    [Fact]
    public async Task Overlapping_entered_times_save_for_different_workers_but_not_for_the_same_worker()
    {
        var (id, c) = await InspectionAsync();
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1).Date.AddHours(13), TimeSpan.Zero);
        var a = await WorkerAsync(c, id, "Made-up Avery");
        var b = await WorkerAsync(c, id, "Made-up Blake");
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, workerId = a, start, stop = start.AddHours(4) });
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, workerId = b, start = start.AddHours(1), stop = start.AddHours(3) });
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 3, start = start.AddHours(1), stop = start.AddHours(2) });
        // Shawn's case: a second nameless crew row overlapping an existing one is a different group of people, so it saves
        await c.PostOkAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, start = start.AddMinutes(20), stop = start.AddHours(4) });
        // a registered person's clock never clashes with day workers or the crew
        await c.PostOkAsync($"{T}/{id}/time/start");
        await c.PostOkAsync($"{T}/{id}/time/stop");

        var r = await c.PostAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, workerId = a, start = start.AddHours(3), stop = start.AddHours(5) });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("overlap Made-up Avery's own time", await r.Content.ReadAsStringAsync());

        // editing an entry never clashes with itself
        var sessions = (await c.GetElAsync($"{T}/{id}/crew")).GetProperty("sessions").EnumerateArray().ToList();
        var mine = sessions.First(s => s.GetProperty("workerId").ValueKind == JsonValueKind.String && s.GetProperty("workerId").GetGuid() == a).Id();
        Assert.True((await c.PutAsync($"{T}/crew/sessions/{mine}", new { crewSize = 1, start, stop = start.AddHours(3.5) })).IsSuccessStatusCode);

        // a worker still clocked on gets a clear message
        await c.PostOkAsync($"{T}/{id}/crew/start", new { crewSize = 1, workerId = b });
        var run = await c.PostAsync($"{T}/{id}/crew/sessions", new { crewSize = 1, workerId = b, start = DateTimeOffset.UtcNow.AddMinutes(-1).AddSeconds(30), stop = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
        Assert.Contains("Made-up Blake is already clocked on to this work order", await run.Content.ReadAsStringAsync());
        await c.PostOkAsync($"{T}/{id}/crew/stop?workerId={b}", new { });
    }

    [Fact]
    public async Task A_person_can_be_clocked_on_to_only_one_job_at_a_time()
    {
        var site = await app.CreateSiteAsync();
        var (_, c) = await app.CreateUserAsync(Lead, site);
        async Task<(Guid, string)> New()
        {
            var j = await (await c.PostOkAsync(T, new { type = "Inspection", siteId = site, inspectionSubType = "AQL", minBenchmarkMinutes = 0, maxBenchmarkMinutes = 600 })).JsonAsync();
            await c.StartAsync(j.Id());
            return (j.Id(), (await c.GetElAsync($"{T}/{j.Id()}")).GetProperty("record").Str("number")!);
        }
        var (one, oneNo) = await New();
        var (two, _) = await New();

        await c.PostOkAsync($"{T}/{one}/time/start");
        var r = await c.PostAsync($"{T}/{two}/time/start", new { });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains($"You're already clocked on to {oneNo}. Clock off there first.", await r.Content.ReadAsStringAsync());
        await c.PostOkAsync($"{T}/{one}/time/stop");
        await c.PostOkAsync($"{T}/{two}/time/start");
        await c.PostOkAsync($"{T}/{two}/time/stop");

        // the same day worker (same name) on two jobs: one clock at a time, and entered times can't overlap across jobs
        var w1 = await WorkerAsync(c, one, "Made-up Casey");
        var w2 = await WorkerAsync(c, two, "made-up casey");
        await c.PostOkAsync($"{T}/{one}/crew/start", new { crewSize = 1, workerId = w1 });
        var s2 = await c.PostAsync($"{T}/{two}/crew/start", new { crewSize = 1, workerId = w2 });
        Assert.Equal(HttpStatusCode.Conflict, s2.StatusCode);
        Assert.Contains($"already clocked on to {oneNo}", await s2.Content.ReadAsStringAsync());
        await c.PostOkAsync($"{T}/{one}/crew/stop?workerId={w1}", new { });

        var start = new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-1).Date.AddHours(13), TimeSpan.Zero);
        await c.PostOkAsync($"{T}/{one}/crew/sessions", new { crewSize = 1, workerId = w1, start, stop = start.AddHours(2) });
        var e2 = await c.PostAsync($"{T}/{two}/crew/sessions", new { crewSize = 1, workerId = w2, start = start.AddHours(1), stop = start.AddHours(3) });
        Assert.Equal(HttpStatusCode.Conflict, e2.StatusCode);
        Assert.Contains(oneNo, await e2.Content.ReadAsStringAsync());
        await c.PostOkAsync($"{T}/{two}/crew/sessions", new { crewSize = 1, workerId = w2, start = start.AddHours(2), stop = start.AddHours(3) });
        // nameless crew rows have no person, so they never block
        await c.PostOkAsync($"{T}/{one}/crew/start", new { crewSize = 2 });
        await c.PostOkAsync($"{T}/{two}/crew/start", new { crewSize = 2 });
        await c.PostOkAsync($"{T}/{one}/crew/stop", new { });
        await c.PostOkAsync($"{T}/{two}/crew/stop", new { });
    }
}
