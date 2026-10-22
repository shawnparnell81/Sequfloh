using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

[Collection("api")]
public sealed class SequflohTrackingTests(TestApp app)
{
    const string U = "/api/sequfloh/tracking";
    static async Task<JsonElement> PostEl(ApiClient c, string url, object body) => await (await c.PostOkAsync(url, body)).JsonAsync();
    static readonly string[] Planner = [Permissions.SequflohAccess, Permissions.SequflohCreateValidationRecord, Permissions.SequflohCreateFaiRecord, Permissions.SequflohCreateRepairRecord,
        Permissions.SequflohCreateProductionTask, Permissions.SequflohDispatchTask, Permissions.SequflohReassignTask, Permissions.SequflohCancelTask];
    static readonly string[] Tech = [Permissions.SequflohAccess, Permissions.SequflohStartTask, Permissions.SequflohPauseTask, Permissions.SequflohResumeTask, Permissions.SequflohHoldTask,
        Permissions.SequflohCompleteTask, Permissions.SequflohFailTask];
    static readonly string[] Quality = [Permissions.SequflohAccess, Permissions.SequflohEditQualityFolder, Permissions.SequflohStartTask];
    static readonly string[] Engineering = [Permissions.SequflohAccess, Permissions.SequflohEditEngineeringFolder];

    /// <summary>Released instruction written straight to the database (the lifecycle itself is covered by SequflohInstructionTests).</summary>
    async Task<Guid> ReleasedAsync(Guid site, WorkInstructionType type, string steps, string chars = "[]", string parts = "[]", Guid? partId = null, string status = "Released", string content = "{}")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wi = new SequflohWorkInstruction { Type = type, Title = $"{type} instruction", Number = "TWI-" + Guid.NewGuid().ToString("N")[..8], Revision = "A", SiteId = site,
            Status = Enum.Parse<WorkInstructionStatus>(status), Steps = steps, Characteristics = chars, PartsRows = parts, Content = content, PartId = partId, RepairDescription = type == WorkInstructionType.RepairWork ? "Component replacement" : null };
        wi.FamilyId = wi.Id;
        db.SequflohWorkInstructions.Add(wi);
        await db.SaveChangesAsync();
        return wi.Id;
    }
    async Task<Guid> PartAsync(Guid site, bool repairable, bool serialized = false)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = new Part { PartNumber = "TP-" + Guid.NewGuid().ToString("N")[..8], Name = "Pump housing", SiteId = site, Status = MasterStatus.Active, IsRepairable = repairable, IsSerialized = serialized };
        db.Parts.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }
    /// <summary>Moves the start of the current state back, as if that much time had passed on the database clock.</summary>
    async Task ElapseAsync(Guid id, int minutes)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sqf.sequfloh_tracking_records SET \"CurrentStateStartedAt\" = \"CurrentStateStartedAt\" - make_interval(mins => {minutes}) WHERE \"Id\" = {id}");
    }
    static async Task<JsonElement> Rec(ApiClient c, Guid id) => await c.GetElAsync($"{U}/{id}");
    static int Ver(JsonElement r) => r.GetProperty("record").GetProperty("stateVersion").GetInt32();
    static async Task<JsonElement> Act(ApiClient c, Guid id, string action, object? extra = null, Guid? op = null)
    {
        var r = await Rec(c, id);
        var body = new Dictionary<string, object?> { ["operationId"] = op ?? Guid.NewGuid(), ["stateVersion"] = Ver(r) };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return await PostEl(c, $"{U}/{id}/actions/{action}", body);
    }
    static async Task<HttpStatusCode> ActStatus(ApiClient c, Guid id, string action, object? extra = null)
    {
        var r = await Rec(c, id);
        var body = new Dictionary<string, object?> { ["operationId"] = Guid.NewGuid(), ["stateVersion"] = Ver(r) };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(extra);
        return (await c.PostAsync($"{U}/{id}/actions/{action}", body)).StatusCode;
    }
    static async Task<HttpResponseMessage> Save(ApiClient c, Guid id, Func<JsonElement, object> body)
    {
        var r = await Rec(c, id);
        return await c.PutAsync($"{U}/{id}", body(r.GetProperty("record")));
    }
    static string Row(JsonElement rec, string list, int i) => rec.GetProperty(list)[i].Str("id");

    [Fact]
    public async Task Validation_record_keeps_its_revision_and_completes_in_one_step_once_results_are_recorded()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (techId, tech) = await app.CreateUserAsync(Tech, site);
        var (_, quality) = await app.CreateUserAsync(Quality, site);
        var (_, viewer) = await app.CreateUserAsync([Permissions.SequflohAccess], site);
        var steps = """[{"id":"11111111-1111-1111-1111-111111111111","sequence":10,"title":"Pressurize","target":150,"lowerLimit":145,"upperLimit":155,"unit":"psi","actualResultRequired":true,"evidenceRequired":"Gauge photo"},{"id":"22222222-2222-2222-2222-222222222222","sequence":20,"title":"Leak check"}]""";
        var wiId = await ReleasedAsync(site, WorkInstructionType.Validation, steps);
        var draft = await ReleasedAsync(site, WorkInstructionType.Validation, steps, status: "Draft");
        var repairWi = await ReleasedAsync(site, WorkInstructionType.RepairWork, "[]");
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsync(U, new { type = "Validation", workInstructionId = draft, siteId = site })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsync(U, new { type = "Validation", workInstructionId = repairWi, siteId = site })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(U, new { type = "Validation", workInstructionId = wiId, siteId = site })).StatusCode);
        var created = await PostEl(planner, U, new { type = "Validation", workInstructionId = wiId, siteId = site, assignedUserId = techId, lotNumber = "LOT-7" });
        Assert.StartsWith("VAL-T-", created.Str("number"));
        var id = created.Id();
        // a later change to the instruction does not change the record's copy
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wi = await db.SequflohWorkInstructions.FirstAsync(x => x.Id == wiId);
            wi.Steps = "[]"; wi.Revision = "B";
            await db.SaveChangesAsync();
        }
        var rec = (await Rec(viewer, id)).GetProperty("record");
        Assert.Equal("Assigned", rec.Str("status"));
        Assert.Equal("A", rec.Str("workInstructionRevision"));
        Assert.Equal(2, rec.GetProperty("steps").GetArrayLength());
        var s1 = Row(rec, "steps", 0); var s2 = Row(rec, "steps", 1);
        // results only while in progress
        Assert.Equal(HttpStatusCode.Conflict, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), lotNumber = "LOT-7",
            steps = new[] { new { id = s1, result = "Pass" } } })).StatusCode);
        await Act(tech, id, "start");
        Assert.Equal(HttpStatusCode.Conflict, (await ActStatus(tech, id, "start"))); // already active
        Assert.Equal(HttpStatusCode.OK, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), lotNumber = "LOT-7",
            steps = new[] { new { id = s1, result = "Pass", actualResult = "150" } } })).StatusCode);
        var full = await Rec(tech, id);
        var problems = full.GetProperty("completionProblems").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Contains(problems, p => p.Contains("Step 10: record the evidence reference"));
        Assert.Contains(problems, p => p.Contains("Step 20: record the result"));
        Assert.Equal("Pending", full.GetProperty("record").Str("result"));
        Assert.Equal(HttpStatusCode.BadRequest, await ActStatus(tech, id, "complete")); // results still missing
        Assert.Equal(HttpStatusCode.OK, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), lotNumber = "LOT-7",
            evidenceReferences = "Photo set 4", steps = new object[] { new { id = s1, result = "Pass", actualResult = "150", evidence = "IMG-0042" }, new { id = s2, result = "Pass" } } })).StatusCode);
        Assert.Equal("Pass", (await Rec(tech, id)).GetProperty("record").Str("result"));
        Assert.Equal(HttpStatusCode.Forbidden, await ActStatus(quality, id, "complete")); // needs the complete permission
        Assert.Equal(HttpStatusCode.NotFound, (await tech.PostAsync($"{U}/{id}/signoffs", new { operationId = Guid.NewGuid(), stateVersion = Ver(await Rec(tech, id)), role = "Technician" })).StatusCode);
        await Act(tech, id, "complete");
        rec = (await Rec(viewer, id)).GetProperty("record");
        Assert.Equal("Completed", rec.Str("status"));
        Assert.False(rec.TryGetProperty("approvalStatus", out _));
        Assert.False(rec.TryGetProperty("signoffs", out _));
        Assert.Equal(HttpStatusCode.Conflict, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), technicianComments = "late" })).StatusCode);
        var events = (await Rec(viewer, id)).GetProperty("events").EnumerateArray().Select(e => e.Str("eventType")).ToList();
        Assert.Equal(["Dispatch", "Start", "RecordUpdate", "RecordUpdate", "Completion"], events);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.AuditEntries.AnyAsync(a => a.EntityId == id && a.Action == "TaskComplete" && a.Description.Contains("in progress -> completed")));
        }
    }

    [Fact]
    public async Task Fai_record_copies_characteristics_and_blocks_incomplete_or_out_of_limit_results()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (_, tech) = await app.CreateUserAsync(Tech, site);
        var (_, eng) = await app.CreateUserAsync(Engineering, site);
        var chars = """[{"id":"a","sequence":10,"characteristicNumber":"1","requirement":"Bore","lowerLimit":5.9,"upperLimit":6.1,"unit":"mm"},{"id":"b","sequence":20,"characteristicNumber":"2","requirement":"Finish"}]""";
        var wiId = await ReleasedAsync(site, WorkInstructionType.Fai, "[]", chars);
        var id = (await PostEl(planner, U, new { type = "Fai", workInstructionId = wiId, siteId = site })).Id();
        await Act(tech, id, "start");
        var rec = (await Rec(tech, id)).GetProperty("record");
        Assert.Equal(2, rec.GetProperty("characteristics").GetArrayLength());
        var c1 = Row(rec, "characteristics", 0); var c2 = Row(rec, "characteristics", 1);
        await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(),
            characteristics = new object[] { new { id = c1, actualResult = "6.3", result = "Pass" }, new { id = c2, result = "Fail", actualResult = "Ra 3.2" } } });
        var problems = (await Rec(tech, id)).GetProperty("completionProblems").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Contains(problems, p => p.Contains("outside the limits"));
        Assert.Contains(problems, p => p.Contains("needs an evidence reference"));
        Assert.Contains(problems, p => p.Contains("inspection result"));
        Assert.Equal("Fail", (await Rec(tech, id)).GetProperty("record").Str("result"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), finalDisposition = "Not a disposition" })).StatusCode);
        await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), finalDisposition = "Accepted",
            characteristics = new object[] { new { id = c1, actualResult = "6.02", result = "Pass" }, new { id = c2, result = "Pass", actualResult = "Ra 1.2" } } });
        Assert.Empty((await Rec(tech, id)).GetProperty("completionProblems").EnumerateArray());
        await Act(tech, id, "complete");
        rec = (await Rec(tech, id)).GetProperty("record");
        Assert.Equal("Completed", rec.Str("status"));
        Assert.Equal("Pass", rec.Str("result"));
        Assert.Equal(HttpStatusCode.NotFound, (await eng.PostAsync($"{U}/{id}/actions/approve", new { operationId = Guid.NewGuid(), stateVersion = Ver(await Rec(tech, id)) })).StatusCode);
    }

    [Fact]
    public async Task Repair_record_validates_quantities_operations_tests_and_disposition_and_failed_work_restarts()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (_, tech) = await app.CreateUserAsync(Tech, site);
        var (_, inspector) = await app.CreateUserAsync(Quality, site);
        var plain = await PartAsync(site, repairable: false);
        var part = await PartAsync(site, repairable: true, serialized: true);
        var steps = """[{"id":"s","sequence":10,"operationStep":"OP-1 Replace seal","title":"Replace seal","technicianAcknowledgement":true,"inspectorVerification":true,"passOrFailRequired":true}]""";
        var parts = """[{"id":"p","replacementPartNumber":"SEAL-9","quantity":2,"required":true,"lotNumberRequired":true}]""";
        var wiId = await ReleasedAsync(site, WorkInstructionType.RepairWork, steps, parts: parts, partId: part);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsync(U, new { type = "Repair", workInstructionId = wiId, siteId = site, partId = plain, serialNumber = "SN1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await planner.PostAsync(U, new { type = "Repair", workInstructionId = wiId, siteId = site })).StatusCode); // serialized part needs a serial
        var created = await PostEl(planner, U, new { type = "Repair", workInstructionId = wiId, siteId = site, serialNumber = "SN-100", repairOrder = "RO-5", customerReturnReference = "RMA-77", reportedCondition = "Leaks at the shaft" });
        Assert.StartsWith("RPR-", created.Str("number"));
        var id = created.Id();
        await Act(tech, id, "start");
        var rec = (await Rec(tech, id)).GetProperty("record");
        Assert.Equal("Component replacement", rec.Str("repairDescription"));
        var s = Row(rec, "steps", 0); var p = Row(rec, "partsRows", 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), serialNumber = "SN-100", repairOrder = "RO-5",
            customerReturnReference = "RMA-77", reportedCondition = "Leaks at the shaft", partsRows = new[] { new { id = p, issuedQuantity = 2, usedQuantity = 3 } } })).StatusCode);
        object Body(JsonElement r, object stepsBody, object partsBody, string? disposition, string? test) => new
        {
            operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), serialNumber = "SN-100", repairOrder = "RO-5", customerReturnReference = "RMA-77",
            reportedCondition = "Leaks at the shaft", observedCondition = "Seal worn", finalDisposition = disposition, steps = stepsBody, partsRows = partsBody, details = new { repairTestResult = test }
        };
        Assert.Equal(HttpStatusCode.OK, (await Save(tech, id, r => Body(r, new[] { new { id = s, result = "Pass", acknowledged = true } }, new[] { new { id = p, issuedQuantity = 3, usedQuantity = 2 } }, null, null))).StatusCode);
        var problems = (await Rec(tech, id)).GetProperty("completionProblems").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(problems, x => x.Contains("lot number"));
        Assert.Contains(problems, x => x.Contains("repair test result"));
        Assert.DoesNotContain(problems, x => x.Contains("disposition")); // disposition and scrap are handled in the ERP
        await Save(inspector, id, r => Body(r, new[] { new { id = s, result = "Pass" } }, new[] { new { id = p, issuedQuantity = 3, usedQuantity = 2, returnedQuantity = 1, lotNumber = "L-9" } },
            "Return to service", "Pass"));
        Assert.Empty((await Rec(tech, id)).GetProperty("completionProblems").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, await ActStatus(tech, id, "fail")); // a reason is required
        await Act(tech, id, "fail", new { reason = "Wrong seal material" });
        rec = (await Rec(tech, id)).GetProperty("record");
        Assert.Equal("Failed", rec.Str("status"));
        Assert.Equal("Wrong seal material", rec.Str("failureReason"));
        await Act(planner, id, "restart", new { reason = "Rework with the correct seal" });
        Assert.Equal("Assigned", (await Rec(tech, id)).GetProperty("record").Str("status"));
    }

    [Fact]
    public async Task Production_task_timing_is_persisted_and_duplicate_or_stale_actions_are_refused()
    {
        var site = await app.CreateSiteAsync();
        var (_, planner) = await app.CreateUserAsync(Planner, site);
        var (techId, tech) = await app.CreateUserAsync(Tech, site);
        var (tech2Id, tech2) = await app.CreateUserAsync(Tech, site);
        var wiId = await ReleasedAsync(site, WorkInstructionType.Validation, """[{"id":"x","sequence":10,"title":"Assemble"}]""");
        var id = (await PostEl(planner, U, new { type = "Production", workInstructionId = wiId, siteId = site, workOrder = "WO-1", operation = "Final assembly", priority = "High",
            minBenchmarkMinutes = 15, maxBenchmarkMinutes = 40 })).Id();
        Assert.Equal("NotStarted", (await Rec(tech, id)).GetProperty("record").Str("status"));
        await Act(planner, id, "dispatch", new { assignedUserId = techId });
        Assert.Contains((await Rec(tech2, id)).GetProperty("actions").EnumerateArray(), a => a.Str("action") == "start"); // anyone can work it, assigned or not
        var op = Guid.NewGuid();
        var first = await Act(tech, id, "start", op: op);
        var again = await PostEl(tech, $"{U}/{id}/actions/start", new { operationId = op, stateVersion = Ver(await Rec(tech, id)) - 1 });
        Assert.Equal(first.GetProperty("stateVersion").GetInt32(), again.GetProperty("stateVersion").GetInt32()); // same operation applied once
        Assert.Equal(HttpStatusCode.Conflict, (await tech.PostAsync($"{U}/{id}/actions/pause", new { operationId = Guid.NewGuid(), stateVersion = 0, reason = "Break" })).StatusCode); // stale
        await ElapseAsync(id, 30);
        Assert.Equal(HttpStatusCode.BadRequest, await ActStatus(tech, id, "pause"));
        await Act(tech, id, "pause", new { reason = "Break" });
        await ElapseAsync(id, 10);
        var paused = (await Rec(tech, id)).GetProperty("record");
        Assert.InRange(paused.GetProperty("elapsedSeconds").GetInt64(), 1800, 1860);
        Assert.InRange(paused.GetProperty("pausedSeconds").GetInt64(), 600, 660);
        await Act(tech, id, "resume");
        await ElapseAsync(id, 20);
        await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), workOrder = "WO-1", priority = "High", result = "Pass" });
        Assert.Equal(HttpStatusCode.Forbidden, (await Save(tech, id, r => new { operationId = Guid.NewGuid(), stateVersion = r.GetProperty("stateVersion").GetInt32(), workOrder = "WO-2", priority = "High", result = "Pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, await ActStatus(planner, id, "reassign", new { assignedUserId = tech2Id, expectedAssignedUserId = tech2Id })); // conflicting reassignment
        Assert.Equal(HttpStatusCode.BadRequest, await ActStatus(tech, id, "complete")); // 50 minutes is outside 15-40 without a reason
        await Act(tech, id, "complete", new { varianceReason = "Fixture changeover" });
        var done = (await Rec(tech, id)).GetProperty("record");
        Assert.Equal("Completed", done.Str("status"));
        Assert.InRange(done.GetProperty("elapsedSeconds").GetInt64(), 3000, 3100);
        Assert.InRange(done.GetProperty("varianceMinutes").GetDecimal(), 10m, 11.5m);
        Assert.Equal(HttpStatusCode.Conflict, await ActStatus(planner, id, "cancel", new { reason = "Order canceled" }));
        Assert.Equal(HttpStatusCode.Conflict, await ActStatus(tech, id, "resume"));
        var events = (await Rec(tech, id)).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(1, events.Count(e => e.Str("eventType") == "Start"));
        Assert.Equal(events.Count, events.Select(e => e.Str("operationId")).Distinct().Count());
        var pause = events.First(e => e.Str("eventType") == "Pause");
        Assert.Equal("Break", pause.Str("reason"));
        Assert.InRange(pause.GetProperty("activeSeconds").GetInt64(), 1800, 1860);

        // a second task: cancel needs a reason; a canceled task cannot complete
        var id2 = (await PostEl(planner, U, new { type = "Production", workInstructionId = wiId, siteId = site, assignedUserId = tech2Id })).Id();
        Assert.Equal(HttpStatusCode.BadRequest, await ActStatus(planner, id2, "cancel"));
        await Act(planner, id2, "cancel", new { reason = "Duplicate task" });
        Assert.Equal(HttpStatusCode.Conflict, await ActStatus(tech2, id2, "complete"));
        // folders and filters
        var open = await tech.GetElAsync($"{U}?folder=production&site={site}&pageSize=100");
        Assert.Equal(2, open.GetProperty("total").GetInt32());
        Assert.Equal(1, (await tech.GetElAsync($"{U}?folder=completed&site={site}&priority=High")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await tech.GetElAsync($"{U}?site={site}&assigned={tech2Id}")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await tech.GetElAsync($"{U}?site={site}&q=WO-1&result=Pass")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await tech.GetElAsync($"{U}?folder=open&site={site}")).GetProperty("total").GetInt32());
        var (_, outsider) = await app.CreateUserAsync(Tech, await app.CreateSiteAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{U}/{id}")).StatusCode);
    }
}
