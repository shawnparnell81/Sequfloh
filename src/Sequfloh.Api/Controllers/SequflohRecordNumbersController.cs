using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sequfloh.Api.Auth;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Execution;

namespace Sequfloh.Api.Controllers.Sequfloh;

/// <summary>Edits the number and title of a record or file. The permission that edits that kind of record is checked by the service.</summary>
[ApiController, Authorize, RequirePermission(Permissions.SequflohAccess), SequflohEnabled, Route("api/sequfloh/records")]
public sealed class SequflohRecordNumbersController(RecordNumberService s) : ControllerBase
{
    [HttpPut("{kind}/{id:guid}/number")]
    public Task<object> Change(string kind, Guid id, RecordNumberInput r, CancellationToken ct) => s.ChangeAsync(kind, id, r, ct);
}
