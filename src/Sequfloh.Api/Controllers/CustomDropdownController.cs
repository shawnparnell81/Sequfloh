using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Administrator-defined dropdowns. Built-in reference lists stay on their own screens.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/custom-dropdowns")]
public sealed class CustomDropdownController(CustomDropdownService dropdowns) : ControllerBase
{
    [HttpGet("screens")]
    public IReadOnlyList<CustomScreen> Screens() => dropdowns.ScreenList();

    [HttpGet] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public Task<List<object>> List(CancellationToken ct) => dropdowns.ListAsync(ct);

    [HttpGet("{id:guid}")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public Task<object> Get(Guid id, CancellationToken ct) => dropdowns.GetAsync(id, ct);

    [HttpPost] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<object> Create(CustomDropdownInput body, CancellationToken ct) => new { id = await dropdowns.CreateAsync(body, ct) };

    [HttpPut("{id:guid}")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Update(Guid id, CustomDropdownInput body, CancellationToken ct) { await dropdowns.UpdateAsync(id, body, ct); return NoContent(); }

    [HttpPost("{id:guid}/delete")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Delete(Guid id, CustomDropdownDelete body, CancellationToken ct) { await dropdowns.DeleteAsync(id, body.Confirm, ct); return NoContent(); }

    /// <summary>Fields to show on a screen. Pass recordId to include the saved choice.</summary>
    [HttpGet("~/api/sequfloh/custom-fields")]
    public Task<List<CustomFieldView>> Fields([FromQuery] string screen, [FromQuery] string? recordType, [FromQuery] Guid? recordId, CancellationToken ct) =>
        dropdowns.ForScreenAsync(screen, recordType, recordId, ct);

    [HttpPut("~/api/sequfloh/custom-fields/{recordType}/{recordId:guid}")]
    public async Task<IActionResult> Save(string recordType, Guid recordId, CustomFieldSave body, CancellationToken ct)
    {
        await dropdowns.SaveValuesAsync(recordType, recordId, body.Values ?? [], body.Label, ct);
        return NoContent();
    }
}

public sealed record CustomDropdownDelete(string? Confirm);
public sealed record CustomFieldSave(List<CustomValueSet>? Values, string? Label);
