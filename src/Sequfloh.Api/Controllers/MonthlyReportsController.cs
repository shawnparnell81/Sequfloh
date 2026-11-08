using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Monthly quality reports. Viewing, editing and finalizing are separate permissions.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/monthly-reports")]
public sealed class MonthlyReportsController(MonthlyReportService svc) : ControllerBase
{
    [HttpGet("sources")] public object Sources() => svc.Sources();
    [HttpGet("people")] public Task<IReadOnlyList<object>> People(CancellationToken ct) => svc.PeopleAsync(ct);
    [HttpGet] public Task<IReadOnlyList<object>> List(CancellationToken ct) => svc.ListAsync(ct);
    [HttpGet("{id:guid}")] public Task<object> Get(Guid id, CancellationToken ct) => svc.GetAsync(id, ct);
    [HttpPost] public Task<object> Create(CreateRequest r, CancellationToken ct) => svc.CreateAsync(r.Year, r.Month, r.CopyText, ct);

    public sealed record CreateRequest(int Year, int Month, bool CopyText = false);
    public sealed record SaveRequest(MonthlyReportBody Body, decimal FlatRate, uint RowVersion = 0);
    public sealed record ReopenRequest(string? Reason);

    [HttpPut("{id:guid}")] public Task<object> Update(Guid id, SaveRequest r, CancellationToken ct) => svc.UpdateAsync(id, r.Body ?? new MonthlyReportBody(), r.FlatRate, r.RowVersion, ct);
    [HttpPost("{id:guid}/fill")] public Task<object> Fill(Guid id, CancellationToken ct) => svc.FillAsync(id, ct);
    [HttpPost("{id:guid}/finalize")] public Task<object> Finalize(Guid id, CancellationToken ct) => svc.FinalizeAsync(id, ct);
    [HttpPost("{id:guid}/reopen")] public Task<object> Reopen(Guid id, ReopenRequest r, CancellationToken ct) => svc.ReopenAsync(id, r.Reason, ct);

    [HttpPost("{id:guid}/sources"), RequestSizeLimit(MonthlyReportService.MaxBytes + 64 * 1024), RequestFormLimits(MultipartBodyLengthLimit = MonthlyReportService.MaxBytes + 64 * 1024)]
    public async Task<object> Upload(Guid id, [FromForm] string type, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new AppException("Choose a file.");
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await svc.UploadAsync(id, type, file.FileName, file.ContentType, ms.ToArray(), ct);
    }

    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken ct)
    {
        var (b, n) = await svc.PdfAsync(id, ct);
        return InlinePdf.Send(this, b, n);
    }

    [HttpGet("{id:guid}/docx")]
    public async Task<IActionResult> Docx(Guid id, CancellationToken ct)
    {
        var (b, n) = await svc.DocxAsync(id, ct);
        return File(b, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", n);
    }

    [HttpGet("{id:guid}/images/{slot}")]
    public async Task<IActionResult> Image(Guid id, string slot, CancellationToken ct)
    {
        var jpeg = await svc.ImageAsync(id, slot, ct);
        return File(jpeg, "image/jpeg");
    }
}
