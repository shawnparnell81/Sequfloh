using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Job quantity, parts replaced, tester result files and the job report. Anyone who can see the work order can update these.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/tracking")]
public sealed class SequflohJobController(SequflohJobService job, SequflohPrintService print) : ControllerBase
{
    public sealed record ColumnsRequest(string? ResultColumn, List<string>? PassValues, List<string>? ValueColumns, string? BatchColumn, string? DateColumn);

    [HttpGet("{id:guid}/job")] public Task<object> Get(Guid id, CancellationToken ct) => job.GetAsync(id, ct);
    [HttpPost("{id:guid}/job/quantity")] public async Task<object> Quantity(Guid id, QuantityInput r, CancellationToken ct) { await job.UpdateQuantityAsync(id, r, ct); return await job.GetAsync(id, ct); }
    [HttpPut("{id:guid}/job/parts")] public async Task<object> Parts(Guid id, List<PartReplaced> r, CancellationToken ct) { await job.SetPartsAsync(id, r, ct); return await job.GetAsync(id, ct); }
    [HttpPut("{id:guid}/job/columns")]
    public async Task<object> Columns(Guid id, ColumnsRequest r, CancellationToken ct)
    { await job.SaveColumnsAsync(id, new TestColumnMap(r.ResultColumn, r.PassValues, r.ValueColumns, r.BatchColumn, r.DateColumn), ct); return await job.GetAsync(id, ct); }

    [HttpPost("{id:guid}/job/test-files"), RequestSizeLimit(200 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)]
    public async Task<object> Upload(Guid id, [FromForm] List<IFormFile> files, [FromForm] string? batch, [FromForm] DateOnly? testDate, [FromForm] string? kind, CancellationToken ct)
    {
        var list = new List<(string, string, byte[])>();
        foreach (var file in files)
        {
            using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct);
            list.Add((file.FileName, file.ContentType, ms.ToArray()));
        }
        return await job.UploadAsync(id, list, batch, testDate, ct, kind ?? "test");
    }
    [HttpGet("job/test-files/{fileId:guid}")]
    public async Task<IActionResult> Download(Guid fileId, CancellationToken ct) { var (b, n, t) = await job.DownloadAsync(fileId, ct); return File(b, t, n); }
    [HttpGet("job/test-files/{fileId:guid}/columns")] public Task<object> FileColumns(Guid fileId, CancellationToken ct) => job.ColumnsAsync(fileId, ct);
    [HttpDelete("job/test-files/{fileId:guid}")] public async Task<IActionResult> DeleteFile(Guid fileId, [FromQuery] string? reason, CancellationToken ct) { await job.DeleteFileAsync(fileId, ct, reason); return NoContent(); }

    public sealed record NoteRequest(string? Text);
    [HttpGet("{id:guid}/job/notes")] public Task<object> Notes(Guid id, CancellationToken ct) => job.NotesAsync(id, ct);
    [HttpPost("{id:guid}/job/notes")] public Task<object> AddNote(Guid id, NoteRequest r, CancellationToken ct) => job.AddNoteAsync(id, r.Text, ct);
    [HttpPut("job/notes/{noteId:guid}")] public async Task<IActionResult> EditNote(Guid noteId, NoteRequest r, CancellationToken ct) { await job.EditNoteAsync(noteId, r.Text, ct); return NoContent(); }

    [HttpPut("{id:guid}/job/findings")] public async Task<object> SaveFindings(Guid id, JobFindings r, CancellationToken ct) { await job.SaveFindingsAsync(id, r, ct); return await job.GetAsync(id, ct); }
    [HttpPut("{id:guid}/job/cost-lines")] public async Task<object> SaveCostLines(Guid id, List<CostLine> r, CancellationToken ct) { await job.SaveCostLinesAsync(id, r, ct); return await job.GetAsync(id, ct); }
    [HttpPut("{id:guid}/job/report-options")] public async Task<object> SaveReportOptions(Guid id, ReportOptions r, CancellationToken ct) { await job.SaveOptionsAsync(id, r, ct); return await job.GetAsync(id, ct); }
    [HttpGet("{id:guid}/job/report")] public Task<SequflohJobService.JobReport> Report(Guid id, CancellationToken ct) => job.ReportAsync(id, ct);
    [HttpGet("{id:guid}/job/report/pdf")]
    public async Task<IActionResult> Pdf(Guid id, [FromQuery] string? supplier, [FromQuery] string? reference, [FromQuery] decimal? rate, [FromQuery] bool? full, CancellationToken ct)
    {
        var (pdf, name) = await job.PdfAsync(id, supplier, reference, rate, print, ct, full == true);
        return File(pdf, "application/pdf", name);
    }

    /// <summary>Saves the job report as it is now to the saved reports under Reports.</summary>
    [HttpPost("{id:guid}/job/report/save")]
    public async Task<object> SaveReport(Guid id, CancellationToken ct) { var x = await job.SaveReportAsync(id, "Saved", print, ct); return new { x.Id, x.CreatedAt }; }

    /// <summary>The work order as a read-only document (the full job report PDF), shown in the browser.</summary>
    [HttpGet("{id:guid}/job/report/view")]
    public async Task<IActionResult> View(Guid id, CancellationToken ct)
    {
        var (pdf, name) = await job.PdfAsync(id, null, null, null, print, ct);
        return InlinePdf.Send(this, pdf, name);
    }
}

/// <summary>Saved job reports and chargeback reports under Reports. Viewing needs the reports permission.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/saved-reports")]
public sealed class SequflohSavedReportsController(SequflohJobService job) : ControllerBase
{
    [HttpGet] public Task<object> List([FromQuery] string? q, [FromQuery] string? type, CancellationToken ct) => job.SavedReportsAsync(q, type, ct);
    [HttpGet("job/{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken ct)
    {
        var (pdf, name) = await job.SavedJobReportPdfAsync(id, ct);
        return InlinePdf.Send(this, pdf, name);
    }
}

/// <summary>A PDF shown in the browser's own PDF viewer in a new tab, with the file name kept.</summary>
public static class InlinePdf
{
    public static IActionResult Send(ControllerBase c, byte[] pdf, string name)
    {
        c.Response.Headers.ContentDisposition = $"inline; filename=\"{name.Replace("\"", "")}\"";
        c.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        c.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; object-src 'self'; frame-ancestors 'self'";
        return c.File(pdf, "application/pdf");
    }
}
