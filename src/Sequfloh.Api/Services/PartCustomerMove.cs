using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Moves parts the user can already edit to another customer, or back to Unassigned. Mode "ids" uses the ticked parts.
/// Mode "filter" uses every part matching the Part Master list (customer tab, product line, search and the other filters), so a whole product line moves in one request.
/// </summary>
public sealed record MoveCustomerRequest(string Mode, Guid? CustomerId = null, bool ToUnassigned = false, List<Guid>? PartIds = null,
    string? Q = null, string? View = null, string? Family = null, string? Status = null, string? PartType = null, string? Customer = null, string? Type = null);

public sealed record MoveConflict(Guid Id, string PartNumber, string? Line, string Reason);
public sealed record MoveFrom(string Customer, int Count);
public sealed record MoveCustomerPlan(int Move, int AlreadyThere, int Conflicts, int LeftOut, string Target, List<MoveFrom> From, List<MoveConflict> ConflictRows);

/// <summary>Changes only CustomerId. Part IDs, drawings, revisions, work orders and every other link stay as they are.</summary>
public sealed class PartCustomerMoveService(AppDbContext db, ModuleSupport m, SequflohPartsService parts, SequflohFoundationService f)
{
    const int Listed = 100;

    public Task<MoveCustomerPlan> PreviewAsync(MoveCustomerRequest r, CancellationToken ct) => PlanAsync(r, ct);

    public async Task<MoveCustomerPlan> ApplyAsync(MoveCustomerRequest r, CancellationToken ct)
    {
        var (plan, ok) = await BuildAsync(r, ct);
        if (ok.Count == 0) return plan;
        var targetId = r.ToUnassigned ? null : r.CustomerId;
        var names = await CustomerNamesAsync(ct);
        var targetName = TargetName(r, names);
        var now = m.Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var chunk in ok.Select(p => p.Id).Chunk(1000))
        {
            var ids = chunk.ToList();
            await db.Parts.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.CustomerId, targetId)
                .SetProperty(p => p.UpdatedAt, now)
                .SetProperty(p => p.UpdatedById, m.UserId), ct);
        }
        foreach (var chunk in ok.Chunk(400))
        {
            foreach (var p in chunk)
                f.Audit("Moved customer", "Part", p.Id, $"Moved part {p.PartNumber} from {Name(names, p.CustomerId)} to {targetName}. The part and its drawings, revisions, work orders and links were kept.", p.SiteId);
            await db.SaveChangesAsync(ct);
        }
        var from = ok.GroupBy(p => Name(names, p.CustomerId)).OrderBy(g => g.Key).Select(g => $"{g.Count()} from {g.Key}");
        var skipped = plan.Conflicts == 0 ? "None were skipped." : $"{plan.Conflicts} skipped because the same part number is already used in that product line and site.";
        f.Audit("Moved customer", "PartCustomerMove", Guid.CreateVersion7(), $"Moved {ok.Count} part{(ok.Count == 1 ? "" : "s")} to {targetName}: {string.Join(", ", from)}. {skipped}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return plan;
    }

    async Task<MoveCustomerPlan> PlanAsync(MoveCustomerRequest r, CancellationToken ct) => (await BuildAsync(r, ct)).Plan;

    async Task<(MoveCustomerPlan Plan, List<MoveRow> Ok)> BuildAsync(MoveCustomerRequest r, CancellationToken ct)
    {
        var names = await CustomerNamesAsync(ct);
        var targetName = await RequireTargetAsync(r, names, ct);
        var rows = await SelectedAsync(r, ct);
        var targetId = r.ToUnassigned ? null : r.CustomerId;
        var (ok, conflicts) = await ClassifyAsync(rows, targetId, names, await LineNamesAsync(ct), await SiteNamesAsync(ct), ct);
        var from = ok.GroupBy(p => Name(names, p.CustomerId)).OrderBy(g => g.Key).Select(g => new MoveFrom(g.Key, g.Count())).ToList();
        var leftOut = r.Mode == "ids" ? Math.Max(0, (r.PartIds ?? []).Distinct().Count() - rows.Count) : 0;
        var plan = new MoveCustomerPlan(ok.Count, rows.Count - ok.Count - conflicts.Count, conflicts.Count, leftOut, targetName, from, conflicts.Take(Listed).ToList());
        return (plan, ok);
    }

    async Task<string> RequireTargetAsync(MoveCustomerRequest r, Dictionary<Guid, string> names, CancellationToken ct)
    {
        if (r.ToUnassigned) return "Unassigned";
        if (r.CustomerId is not { } id) throw new AppException("Choose the customer to move the parts to.");
        var customer = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.ListKey == SequflohCatalog.Customers, ct)
            ?? throw new AppException("Choose a customer from the Customers list.");
        if (!customer.IsActive) throw new AppException($"{customer.Label} is deactivated. Choose an active customer, or move the parts to Unassigned.");
        return names.GetValueOrDefault(customer.Id) ?? customer.Label;
    }

    async Task<List<MoveRow>> SelectedAsync(MoveCustomerRequest r, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.MasterDataEdit, ct) || !await m.CanAnyAsync(Permissions.SequflohEditParts, ct)) throw new ForbiddenException();
        var q = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohEditParts, ct);
        if (r.Mode == "ids")
        {
            var ids = (r.PartIds ?? []).Distinct().ToList();
            if (ids.Count == 0) throw new AppException("Tick at least one part.");
            q = q.Where(p => ids.Contains(p.Id));
        }
        else if (r.Mode == "filter")
        {
            var status = Enum.TryParse<MasterStatus>(r.Status, true, out var st) ? st : (MasterStatus?)null;
            var type = Enum.TryParse<PartType>(r.Type, true, out var pt) ? pt : (PartType?)null;
            var filtered = await parts.PartsQueryAsync(r.Q, r.View, r.Family, status, type, ct, r.PartType, r.Customer);
            q = q.Where(p => filtered.Select(x => x.Id).Contains(p.Id));
        }
        else throw new AppException("Choose the ticked parts, or all parts matching the list.");
        return await q.OrderBy(p => p.PartNumber).ThenBy(p => p.Id).Select(p => new MoveRow(p.Id, p.PartNumber, p.PartNumberKey, p.SiteId, p.TypeId, p.CustomerId)).ToListAsync(ct);
    }

    async Task<(List<MoveRow> Ok, List<MoveConflict> Conflicts)> ClassifyAsync(List<MoveRow> rows, Guid? targetId, Dictionary<Guid, string> customers, Dictionary<Guid, string> lines, Dictionary<Guid, string> sites, CancellationToken ct)
    {
        var moving = rows.Where(p => p.CustomerId != targetId).ToList();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var keys = moving.Select(p => p.PartNumberKey).Distinct().ToList();
        foreach (var chunk in keys.Chunk(500))
        {
            var keyList = chunk.ToList();
            var atTarget = targetId is null
                ? db.Parts.AsNoTracking().Where(p => p.CustomerId == null && keyList.Contains(p.PartNumberKey))
                : db.Parts.AsNoTracking().Where(p => p.CustomerId == targetId && keyList.Contains(p.PartNumberKey));
            foreach (var existing in await atTarget.Select(p => new { p.SiteId, p.TypeId, p.PartNumberKey }).ToListAsync(ct))
                taken.Add(Slot(existing.SiteId, existing.TypeId, existing.PartNumberKey));
        }
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = new List<MoveConflict>();
        var ok = new List<MoveRow>();
        var who = TargetLabel(targetId, customers);
        foreach (var p in moving)
        {
            var slot = Slot(p.SiteId, p.TypeId, p.PartNumberKey);
            var line = p.TypeId is { } t && lines.TryGetValue(t, out var label) ? label : "no product line";
            var site = sites.GetValueOrDefault(p.SiteId) ?? "this site";
            if (taken.Contains(slot))
                conflicts.Add(new MoveConflict(p.Id, p.PartNumber, line, $"{who} already has part {p.PartNumber} in {line} at {site}. This part was not moved."));
            else if (!claimed.Add(slot))
                conflicts.Add(new MoveConflict(p.Id, p.PartNumber, line, $"Another part in this move has part number {p.PartNumber} in {line} at {site}. This part was not moved."));
            else ok.Add(p);
        }
        return (ok, conflicts);
    }

    static string Slot(Guid site, Guid? type, string key) => $"{site:N}|{type:N}|{key}";
    static string Name(Dictionary<Guid, string> names, Guid? id) => id is { } g && names.TryGetValue(g, out var n) ? n : "Unassigned";
    static string TargetName(MoveCustomerRequest r, Dictionary<Guid, string> names) => r.ToUnassigned ? "Unassigned" : Name(names, r.CustomerId);
    static string TargetLabel(Guid? id, Dictionary<Guid, string> names) => Name(names, id);

    async Task<Dictionary<Guid, string>> CustomerNamesAsync(CancellationToken ct) =>
        await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.Customers).ToDictionaryAsync(v => v.Id, v => v.Label, ct);

    async Task<Dictionary<Guid, string>> LineNamesAsync(CancellationToken ct) =>
        await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies).ToDictionaryAsync(v => v.Id, v => v.Label, ct);

    async Task<Dictionary<Guid, string>> SiteNamesAsync(CancellationToken ct) =>
        await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Code + " · " + s.Name, ct);

    sealed record MoveRow(Guid Id, string PartNumber, string PartNumberKey, Guid SiteId, Guid? TypeId, Guid? CustomerId);
}
