using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;
using Sequfloh.Tracking;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>The shop-day clock: arrival, indirect tasks, breaks, timesheets and corrections. Not a payroll clock.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/day-clock")]
public sealed class SequflohDayClockController(SequflohDayClockService day) : ControllerBase
{
    public sealed record IndirectRequest(Guid TaskId, string? Note, bool ClockIn = false);
    public sealed record PauseRequest(string? Reason);

    [HttpGet] public Task<object> Status(CancellationToken ct) => day.StatusAsync(ct);
    [HttpPost("in")] public Task<object> In(CancellationToken ct) => day.ClockInAsync(ct);
    [HttpPost("out")] public Task<object> Out(CancellationToken ct) => day.ClockOutAsync(ct);
    [HttpPost("late")] public Task<object> Late(CancellationToken ct) => day.WorkLateAsync(ct);
    [HttpPost("pause")] public Task<object> Pause(PauseRequest r, CancellationToken ct) => day.PauseAsync(r.Reason, ct);
    [HttpPost("resume")] public Task<object> Resume(CancellationToken ct) => day.ResumeAsync(ct);
    [HttpPost("indirect")] public Task<object> Indirect(IndirectRequest r, CancellationToken ct) => day.StartIndirectAsync(r.TaskId, r.Note, r.ClockIn, ct);

    [HttpGet("timesheet")]
    public Task<object> Timesheet(Guid? userId, DateOnly? from, DateOnly? to, CancellationToken ct) => day.TimesheetAsync(userId, from, to, ct);

    [HttpGet("team")] public Task<object> Team(CancellationToken ct) => day.TeamAsync(ct);

    [HttpGet("history")] public Task<object> History(Guid? userId, CancellationToken ct) => day.HistoryAsync(userId, ct);

    [HttpPost("corrections")]
    public Task<object> Correct(SequflohDayClockService.CorrectionInput r, CancellationToken ct) => day.CorrectAsync(r, ct);

    [HttpGet("export.csv")]
    public async Task<IActionResult> Csv(Guid? userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var csv = await day.CsvAsync(userId, from, to, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "day-clock.csv");
    }
}
