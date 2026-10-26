using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Sequfloh.Api.Auth;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>
/// Blocks SEQUFLOH endpoints while the module is switched off in General Settings; holders of sequfloh.manage_settings keep access so they can switch it back on.
/// </summary>
public sealed class SequflohEnabledAttribute : TypeFilterAttribute
{
    public SequflohEnabledAttribute() : base(typeof(Filter)) { }
    sealed class Filter(SequflohFoundationService foundation, IAccessService access, ICurrentUser user) : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var ct = context.HttpContext.RequestAborted;
            if (!await foundation.ModuleEnabledAsync(ct) && !(user.UserId is { } uid && await access.HasPermissionAsync(uid, Permissions.SequflohManageSettings, ct)))
                throw new AppException("SEQUFLOH is switched off for this organization.", 403);
            await next();
        }
    }
}

public sealed record SettingsSaveRequest(JsonElement Value, uint RowVersion);
public sealed record AccessRequest(string? Page);
public sealed record ResetRequest(bool Confirmed);

/// <summary>SEQUFLOH foundation: module context, settings, reference data, module access, recent and pinned items.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh")]
public sealed class SequflohFoundationController(SequflohFoundationService f) : ControllerBase
{
    [HttpGet("context")] public Task<object> Context(CancellationToken ct) => f.ContextAsync(ct);
    [HttpPost("access")] public async Task<IActionResult> Access(AccessRequest r, CancellationToken ct) { await f.RecordAccessAsync((r.Page ?? "").Length > 100 ? r.Page![..100] : r.Page ?? "", ct); return NoContent(); }

    // settings (read by every module user: dropdown rules, thresholds, display options)
    [HttpGet("settings")] public Task<SettingsDto> Settings(CancellationToken ct) => f.GetSettingsAsync(ct);
    [HttpPut("settings/{section}")] [RequirePermission(Permissions.SequflohManageSettings)]
    public async Task<IActionResult> SaveSettings(string section, SettingsSaveRequest r, CancellationToken ct) { await f.SaveSectionAsync(section, r.Value, r.RowVersion, ct); return NoContent(); }


    // reference data
    [HttpGet("reference")] public Task<object> Lists(CancellationToken ct) => f.ListsAsync(ct);
    [HttpGet("reference/{key}")] public async Task<object> Values(string key, [FromQuery] string? q, [FromQuery] bool? active, CancellationToken ct) => await f.ValuesAsync(key, q, active, ct);
    [HttpGet("reference/{key}/options")] public Task<List<string>> Options(string key, CancellationToken ct) => f.OptionsAsync(key, ct);
    [HttpPost("reference/{key}")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<object> CreateValue(string key, ReferenceValueInput r, CancellationToken ct) { var v = await f.CreateValueAsync(key, r, ct); return new { v.Id, v.Code }; }
    [HttpPut("reference/values/{id:guid}")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> UpdateValue(Guid id, ReferenceValueInput r, CancellationToken ct) { await f.UpdateValueAsync(id, r, ct); return NoContent(); }
    public sealed record ValidationFormInput(Guid? BlankFormId);
    /// <summary>Validation forms by product: one row per product family with its confirmed blank form and a suggestion that is never saved on its own.</summary>
    [HttpGet("validation-forms")] public Task<object> ValidationForms(CancellationToken ct) => f.ValidationFormsAsync(ct);
    [HttpPut("validation-forms/{familyId:guid}")] [RequirePermission(Permissions.SequflohManageSettings)]
    public async Task<IActionResult> SetValidationForm(Guid familyId, ValidationFormInput r, CancellationToken ct) { await f.SetValidationFormAsync(familyId, r.BlankFormId, ct); return NoContent(); }
    [HttpPut("validation-forms/tests/{key}")] [RequirePermission(Permissions.SequflohManageSettings)]
    public async Task<IActionResult> SetTestForm(string key, ValidationFormInput r, CancellationToken ct) { await f.SetTestFormAsync(key, r.BlankFormId, ct); return NoContent(); }
    public sealed record HiddenFieldsInput(List<string>? Fields);
    [HttpPut("reference/values/{id:guid}/hidden-fields")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> FamilyHiddenFields(Guid id, HiddenFieldsInput r, CancellationToken ct) { await f.SetFamilyHiddenFieldsAsync(id, r.Fields, ct); return NoContent(); }
    public sealed record NumberCodeInput(string? Code);
    [HttpPut("reference/values/{id:guid}/number-code")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> FamilyNumberCode(Guid id, NumberCodeInput r, CancellationToken ct) { await f.SetFamilyNumberCodeAsync(id, r.Code, ct); return NoContent(); }
    [HttpPost("reference/values/{id:guid}/activate")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) { await f.SetActiveAsync(id, true, ct); return NoContent(); }
    [HttpPost("reference/values/{id:guid}/deactivate")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) { await f.SetActiveAsync(id, false, ct); return NoContent(); }
    [HttpDelete("reference/values/{id:guid}")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> DeleteValue(Guid id, CancellationToken ct) { await f.DeleteValueAsync(id, ct); return NoContent(); }
    [HttpPost("reference/values/{id:guid}/default")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Default(Guid id, CancellationToken ct) { await f.SetDefaultAsync(id, ct); return NoContent(); }
    [HttpGet("reference/values/{id:guid}/history")] public Task<object> History(Guid id, CancellationToken ct) => f.ValueHistoryAsync(id, ct);
    [HttpPost("reference/{key}/reorder")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<IActionResult> Reorder(string key, ReorderInput r, CancellationToken ct) { await f.ReorderAsync(key, r, ct); return NoContent(); }
    [HttpPost("reference/{key}/reset")] [RequirePermission(Permissions.SequflohManageDropdowns)]
    public async Task<object> Reset(string key, ResetRequest r, CancellationToken ct) => new { Changed = await f.ResetListAsync(key, r.Confirmed, ct) };
    [HttpPost("seed")] [RequirePermission(Permissions.SequflohManageSettings)]
    public async Task<object> Seed(CancellationToken ct) => new { Added = await f.EnsureSeededAsync(ct) };

    // module access on the existing roles
    [HttpGet("module-access")] [RequirePermission(Permissions.SequflohManageModuleAccess)] public Task<object> ModuleAccess(CancellationToken ct) => f.ModuleAccessAsync(ct);
    [HttpPut("module-access/roles/{roleId:guid}")] [RequirePermission(Permissions.SequflohManageModuleAccess)]
    public async Task<IActionResult> SetRole(Guid roleId, RoleAccessInput r, CancellationToken ct) { await f.SetRoleAccessAsync(roleId, r, ct); return NoContent(); }

    // recent and pinned items of the current user
    [HttpGet("my-items")] public Task<object> MyItems(CancellationToken ct) => f.UserItemsAsync(ct);
    [HttpPost("my-items")] public async Task<IActionResult> Touch(UserItemInput r, CancellationToken ct) { await f.TouchAsync(r, ct); return NoContent(); }
    [HttpGet("my-home")] public async Task<object> MyHome(CancellationToken ct) => new { Sections = await f.HomeLayoutAsync(ct) };
    [HttpPut("my-home")] public async Task<IActionResult> SaveHome(List<HomeSection> r, CancellationToken ct) { await f.SaveHomeLayoutAsync(r, ct); return NoContent(); }
    [HttpDelete("my-home")] public async Task<IActionResult> ResetHome(CancellationToken ct) { await f.ResetHomeLayoutAsync(ct); return NoContent(); }
    [HttpDelete("my-items/favorites/{id:guid}")] public async Task<IActionResult> Unpin(Guid id, CancellationToken ct) { await f.RemoveFavoriteAsync(id, ct); return NoContent(); }
}
