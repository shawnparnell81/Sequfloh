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
    public async Task<object> Upload(Guid id, [FromForm] List<IFormFile> files, [FromForm] string? batch, [FromForm] DateOnly? testDate, CancellationToken ct)
    {
        var list = new List<(string, string, byte[])>();
        foreach (var file in files)
        {
            using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct);
            list.Add((file.FileName, file.ContentType, ms.ToArray()));
        }
        return await job.UploadAsync(id, list, batch, testDate, ct);
    }
    [HttpGet("job/test-files/{fileId:guid}")]
    public async Task<IActionResult> Download(Guid fileId, CancellationToken ct) { var (b, n, t) = await job.DownloadAsync(fileId, ct); return File(b, t, n); }
    [HttpGet("job/test-files/{fileId:guid}/columns")] public Task<object> FileColumns(Guid fileId, CancellationToken ct) => job.ColumnsAsync(fileId, ct);
    [HttpDelete("job/test-files/{fileId:guid}")] public async Task<IActionResult> DeleteFile(Guid fileId, CancellationToken ct) { await job.DeleteFileAsync(fileId, ct); return NoContent(); }

    [HttpGet("{id:guid}/job/report")] public Task<SequflohJobService.JobReport> Report(Guid id, CancellationToken ct) => job.ReportAsync(id, ct);
    [HttpGet("{id:guid}/job/report/pdf")]
    public async Task<IActionResult> Pdf(Guid id, [FromQuery] string? supplier, [FromQuery] string? reference, [FromQuery] decimal? rate, CancellationToken ct)
    {
        var (pdf, name) = await job.PdfAsync(id, supplier, reference, rate, print, ct);
        return File(pdf, "application/pdf", name);
    }
}
