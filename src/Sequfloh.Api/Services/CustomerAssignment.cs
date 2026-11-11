using System.Text;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Drawings;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>One confirmed exception: the customer to set, or null to clear the customer. Part numbers and links are not changed.</summary>
public sealed record CustomerAssignmentException(Guid PartId, Guid? CustomerId);

/// <summary>
/// Confirm customer assignment. AssignClear sets the customer on every clear suggestion the caller can edit, except SkipPartIds.
/// Exceptions are explicit choices, including a customer for an ambiguous or unassigned part, and they override a clear suggestion.
/// </summary>
public sealed record CustomerAssignmentConfirm(bool AssignClear, List<Guid>? SkipPartIds = null, List<CustomerAssignmentException>? Exceptions = null, string? Q = null);

public sealed record CustomerAssignmentCustomer(Guid Id, string Label);

public sealed record CustomerAssignmentRow(
    Guid Id, string PartNumber, string Name, string? Family, string? DrawingNumber, string? DrawingRevision,
    Dictionary<string, string> Aliases, Guid? CustomerId, string? CustomerName,
    string Outcome, Guid? ProposedCustomerId, string? ProposedCustomer, string Reason,
    int WorkOrders, int InspectionRequests, int Fai, int Validation, int Ncr, int DrawingLinks, int WorkInstructions, int PartsLists, int ComponentLinks,
    string? WorkOrderNumbers);

public sealed record CustomerAssignmentPreview(
    int Total, int Clear, int Ambiguous, int Unassigned, int Assigned, int Page, int PageSize,
    List<CustomerAssignmentCustomer> Customers, List<CustomerAssignmentRow> Rows);

public sealed record CustomerAssignmentResult(int Assigned, int Unchanged, int Cleared);

/// <summary>
/// Suggests a customer for existing parts from the primary part number and the customer-number columns already stored on the part.
/// Alias columns that do not equal the primary number never assign a part. Confirming writes CustomerId only.
/// </summary>
public sealed class CustomerAssignmentService(AppDbContext db, ModuleSupport m, SequflohFoundationService f)
{
    public const string Clear = "Clear";
    public const string Ambiguous = "Ambiguous";
    public const string Unassigned = "Unassigned";
    public const string Assigned = "Assigned";
    /// <summary>A saved customer, and a clear suggestion that names a different customer. Not applied by Assign clear suggestions.</summary>
    public const string Different = "Different";

    /// <summary>
    /// CSA columns that hold one customer's own number, keyed by the code the Customers list assigns to that name.
    /// NAPA PN is not here: it does not say NAPA US or NAPA Canada. Nissan is not seeded; a customer an administrator adds with code NISSAN uses the Nissan VA # column.
    /// </summary>
    public static readonly (string Code, string Attribute)[] Evidence =
    [
        ("O_REILLY", "orNumber"),
        ("NAPA_US", "napaUs"),
        ("NAPA_CANADA", "napaCa"),
        ("NISSAN", "nissanVa"),
    ];

    static readonly string[] AliasKeys = ["orNumber", "napaUs", "napaCa", "nissanVa", "napaPn", "dmaPn", "position", "keyApplication"];

    sealed record CustomerRow(Guid Id, string Code, string Label, bool IsActive);

    sealed record Source(Guid Id, string PartNumber, string Name, string? ProductFamily, string? DrawingNumber, string? DrawingRevision, string? Attributes, Guid? CustomerId, Guid SiteId);

    sealed class Draft
    {
        public required Source Part { get; init; }
        public required Dictionary<string, string> Aliases { get; init; }
        public required string Outcome { get; init; }
        public Guid? ProposedCustomerId { get; init; }
        public string? ProposedCustomer { get; init; }
        public required string Reason { get; init; }
        public CustomerAssignmentRow ToRow(string? customerName, Deps deps) => new(
            Part.Id, Part.PartNumber, Part.Name, Part.ProductFamily, Part.DrawingNumber, Part.DrawingRevision, Aliases,
            Part.CustomerId, customerName, Outcome, ProposedCustomerId, ProposedCustomer, Reason,
            deps.WorkOrders, deps.InspectionRequests, deps.Fai, deps.Validation, deps.Ncr, deps.DrawingLinks, deps.WorkInstructions, deps.PartsLists, deps.ComponentLinks,
            deps.WorkOrderNumbers);
    }

    sealed class Deps
    {
        public int WorkOrders { get; set; }
        public int InspectionRequests { get; set; }
        public int Fai { get; set; }
        public int Validation { get; set; }
        public int Ncr { get; set; }
        public int DrawingLinks { get; set; }
        public int WorkInstructions { get; set; }
        public int PartsLists { get; set; }
        public int ComponentLinks { get; set; }
        public string? WorkOrderNumbers { get; set; }
        public static Deps Empty { get; } = new();
    }

    async Task<IQueryable<Part>> EditableAsync(CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohEditParts, ct) || !await m.CanAnyAsync(Permissions.MasterDataEdit, ct)) throw new ForbiddenException();
        var parts = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.SequflohEditParts, ct);
        if (await m.HasAllSitesAsync(Permissions.MasterDataEdit, ct)) return parts;
        var edit = await m.ScopeAsync(db.Parts.AsNoTracking(), Permissions.MasterDataEdit, ct);
        return parts.Where(p => edit.Select(x => x.Id).Contains(p.Id));
    }

    async Task<Dictionary<string, CustomerRow>> CustomersByCodeAsync(CancellationToken ct)
    {
        var rows = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.Customers)
            .Select(v => new CustomerRow(v.Id, v.Code, v.Label, v.IsActive)).ToListAsync(ct);
        return rows.GroupBy(v => v.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clear when the primary number equals exactly one active customer's number column. More than one match stays unassigned.
    /// A column that does not equal the primary number is an alias and does not assign the part. A shared drawing is not a match and is not a conflict.
    /// </summary>
    public static (string Outcome, string? Code, string Reason) Classify(string? partNumber, IReadOnlyDictionary<string, string> attrs, string? drawingNumber, IReadOnlyDictionary<string, (string Label, bool Active)> customersByCode)
    {
        static string Trim(string? s) => (s ?? "").Trim();
        static bool Real(string? s) => Trim(s).Length > 0 && DrawingPartMatcher.Normalize(s).Length > 0;
        static bool Same(string? a, string? b) => Real(a) && Real(b) && string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);
        string Col(string key) => PartAttributes.Find(key)?.Label ?? key;

        var hits = new List<(string Code, string Attribute)>();
        foreach (var (code, attr) in Evidence)
            if (attrs.TryGetValue(attr, out var value) && Same(partNumber, value)) hits.Add((code, attr));

        if (hits.Count > 1)
        {
            var names = string.Join(" and ", hits.Select(h => Col(h.Attribute)));
            return (Ambiguous, null, $"The primary number matches {names}. A part stays unassigned when more than one customer number matches. Alias columns do not decide the customer.");
        }
        if (hits.Count == 1)
        {
            var (code, attr) = hits[0];
            var col = Col(attr);
            if (!customersByCode.TryGetValue(code, out var customer))
                return (Unassigned, null, $"The primary number matches {col}, and that customer is not on the Customers list. The part stays unassigned.");
            if (!customer.Active)
                return (Unassigned, null, $"The primary number matches {col}, but {customer.Label} is deactivated. The part stays unassigned.");
            return (Clear, code, $"The primary number matches {col}, so the suggested customer is {customer.Label}. Other customer numbers on the part are aliases and do not assign it.");
        }

        var notes = new List<string>();
        if (attrs.TryGetValue("napaPn", out var napa) && Same(partNumber, napa))
            notes.Add("The primary number matches NAPA PN, which does not say NAPA US or NAPA Canada");
        if (attrs.TryGetValue("dmaPn", out var dma) && Same(partNumber, dma))
            notes.Add("The primary number matches DMA PN, which is not a customer number");
        if (Same(partNumber, drawingNumber))
            notes.Add("The primary number matches the drawing number. That is the supplier number, not a customer");
        if (Evidence.Any(c => attrs.TryGetValue(c.Attribute, out var v) && Real(v) && !Same(partNumber, v)))
            notes.Add("NAPA, Nissan and other alias columns do not assign the part");
        var reason = notes.Count == 0
            ? "The primary number does not match a customer number column, so the part stays unassigned."
            : string.Join(". ", notes) + ". The part stays unassigned.";
        return (Unassigned, null, reason);
    }

    async Task<List<Draft>> DraftsAsync(string? q, string? outcome, CancellationToken ct)
    {
        var query = await EditableAsync(ct);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(p => EF.Functions.ILike(p.PartNumber + " " + p.Name + " " + (p.DrawingNumber ?? "") + " " + (p.AttributeText ?? "") + " " + (p.ProductFamily ?? ""), like));
        }
        var sources = await query.Select(p => new Source(p.Id, p.PartNumber, p.Name, p.ProductFamily, p.DrawingNumber, p.DrawingRevision, p.Attributes, p.CustomerId, p.SiteId)).ToListAsync(ct);
        var customers = await CustomersByCodeAsync(ct);
        var byCode = customers.ToDictionary(kv => kv.Key, kv => (kv.Value.Label, kv.Value.IsActive), StringComparer.OrdinalIgnoreCase);
        var nameById = customers.Values.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Label);
        var want = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim();
        var drafts = new List<Draft>();
        foreach (var p in sources)
        {
            var attrs = PartAttributes.Read(p.Attributes);
            var (result, code, reason) = Classify(p.PartNumber, attrs, p.DrawingNumber, byCode);
            CustomerRow? proposed = null;
            if (result == Clear && code is not null) customers.TryGetValue(code, out proposed);
            var outcomeName = result;
            Guid? proposedId = proposed?.Id;
            string? proposedLabel = proposed?.Label;
            if (p.CustomerId is { } savedId)
            {
                var savedName = nameById.TryGetValue(savedId, out var n) ? n : "the saved customer";
                if (proposed is not null && proposed.Id == savedId)
                {
                    outcomeName = Assigned;
                    proposedId = null;
                    proposedLabel = null;
                    reason = $"Already {savedName}. The saved customer matches the suggestion, so it is not proposed again.";
                }
                else if (proposed is not null)
                {
                    outcomeName = Different;
                    reason = $"Saved customer is {savedName}. The primary number suggests {proposed.Label}. Assign clear suggestions will not change the saved customer. Choose {proposed.Label} on this row to switch.";
                }
                else
                {
                    outcomeName = Assigned;
                    proposedId = null;
                    proposedLabel = null;
                    reason = $"Assigned to {savedName}.";
                }
            }
            if (want is not null && !Matches(outcomeName, want)) continue;
            var aliases = new Dictionary<string, string>();
            foreach (var key in AliasKeys)
                if (attrs.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) aliases[key] = v;
            drafts.Add(new Draft { Part = p, Aliases = aliases, Outcome = outcomeName, ProposedCustomerId = proposedId, ProposedCustomer = proposedLabel, Reason = reason });
        }
        drafts.Sort((a, b) =>
        {
            var n = string.Compare(a.Part.PartNumber, b.Part.PartNumber, StringComparison.OrdinalIgnoreCase);
            return n != 0 ? n : a.Part.Id.CompareTo(b.Part.Id);
        });
        return drafts;
    }

    public async Task<CustomerAssignmentPreview> PreviewAsync(string? q, string? outcome, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(1, page);
        var all = await DraftsAsync(q, null, ct);
        var filtered = string.IsNullOrWhiteSpace(outcome) ? all : all.Where(d => Matches(d.Outcome, outcome.Trim())).ToList();
        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var deps = await DepsAsync(slice.Select(d => d.Part.Id).ToList(), ct);
        var names = await CustomerNamesAsync(ct);
        var customers = names.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Select(kv => new CustomerAssignmentCustomer(kv.Key, kv.Value)).ToList();
        // the dropdown offers active customers only; a deactivated name still shows on a part that already has it
        var activeIds = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.Customers && v.IsActive).Select(v => v.Id).ToListAsync(ct);
        var active = new HashSet<Guid>(activeIds);
        return new CustomerAssignmentPreview(
            filtered.Count, all.Count(d => d.Outcome == Clear), all.Count(d => d.Outcome == Ambiguous), all.Count(d => d.Outcome == Unassigned),
            all.Count(d => d.Outcome is Assigned or Different),
            page, pageSize, customers.Where(c => active.Contains(c.Id)).ToList(),
            slice.Select(d => d.ToRow(d.Part.CustomerId is { } id && names.TryGetValue(id, out var n) ? n : null, deps.GetValueOrDefault(d.Part.Id) ?? Deps.Empty)).ToList());
    }

    public async Task<byte[]> ExportAsync(string? q, string? outcome, CancellationToken ct)
    {
        var drafts = await DraftsAsync(q, outcome, ct);
        if (drafts.Count > 50_000) drafts = drafts.Take(50_000).ToList();
        var deps = await DepsAsync(drafts.Select(d => d.Part.Id).ToList(), ct);
        var names = await CustomerNamesAsync(ct);
        var sb = new StringBuilder();
        var head = new[]
        {
            "Part ID", "Part number", "Name", "Product family", "Drawing #", "Drawing revision",
            "OR #", "NAPA US #", "NAPA CA #", "Nissan VA #", "NAPA PN", "DMA PN", "Position", "Key application",
            "Current customer", "Proposed customer", "Outcome", "Reason",
            "Work orders", "Work order numbers", "Inspection requests", "FAI", "Validation", "NCR", "Drawing links", "Work instructions", "Parts list entries", "Component links",
        };
        sb.Append(string.Join(",", head.Select(Csv))).Append("\r\n");
        foreach (var d in drafts)
        {
            var dep = deps.GetValueOrDefault(d.Part.Id) ?? Deps.Empty;
            string Alias(string key) => d.Aliases.TryGetValue(key, out var v) ? v : "";
            var current = d.Part.CustomerId is { } id && names.TryGetValue(id, out var n) ? n : "";
            var cells = new[]
            {
                d.Part.Id.ToString(), d.Part.PartNumber, d.Part.Name, d.Part.ProductFamily ?? "", d.Part.DrawingNumber ?? "", d.Part.DrawingRevision ?? "",
                Alias("orNumber"), Alias("napaUs"), Alias("napaCa"), Alias("nissanVa"), Alias("napaPn"), Alias("dmaPn"), Alias("position"), Alias("keyApplication"),
                current, d.ProposedCustomer ?? "", d.Outcome, d.Reason,
                dep.WorkOrders.ToString(), dep.WorkOrderNumbers ?? "", dep.InspectionRequests.ToString(), dep.Fai.ToString(), dep.Validation.ToString(), dep.Ncr.ToString(),
                dep.DrawingLinks.ToString(), dep.WorkInstructions.ToString(), dep.PartsLists.ToString(), dep.ComponentLinks.ToString(),
            };
            sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public async Task<CustomerAssignmentResult> ConfirmAsync(CustomerAssignmentConfirm r, CancellationToken ct)
    {
        var exceptions = (r.Exceptions ?? []).GroupBy(x => x.PartId).Select(g => g.Last()).ToList();
        if (!r.AssignClear && exceptions.Count == 0) throw new AppException("Choose at least one part to assign.");
        var customers = await CustomersByCodeAsync(ct);
        var byId = customers.Values.ToDictionary(c => c.Id);
        var skip = new HashSet<Guid>(r.SkipPartIds ?? []);
        var wanted = new Dictionary<Guid, Guid?>();

        if (r.AssignClear)
        {
            foreach (var d in await DraftsAsync(r.Q, Clear, ct))
            {
                if (skip.Contains(d.Part.Id) || d.ProposedCustomerId is null) continue;
                wanted[d.Part.Id] = d.ProposedCustomerId;
            }
        }

        if (exceptions.Count > 0)
        {
            var editable = await EditableAsync(ct);
            var ids = exceptions.Select(e => e.PartId).Distinct().ToList();
            var found = await db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
            if (found.Count != ids.Count) throw new NotFoundException("Part");
            var allowed = await editable.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
            if (allowed.Count != ids.Count) throw new ForbiddenException();
            foreach (var e in exceptions)
            {
                if (e.CustomerId is { } cid && (!byId.TryGetValue(cid, out var customer) || !customer.IsActive))
                    throw new AppException("Choose an active customer. A deactivated customer cannot be assigned.");
                wanted[e.PartId] = e.CustomerId;
            }
        }

        if (wanted.Count == 0) throw new AppException("Nothing to assign. No clear suggestion is selected, and no exception was confirmed.");

        var idList = wanted.Keys.ToList();
        var current = await db.Parts.AsNoTracking().Where(p => idList.Contains(p.Id)).Select(p => new { p.Id, p.PartNumber, p.CustomerId, p.SiteId }).ToListAsync(ct);
        var assigned = 0;
        var unchanged = 0;
        var cleared = 0;
        var updates = new List<(Guid Id, Guid? CustomerId)>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var p in current)
        {
            var target = wanted[p.Id];
            if (p.CustomerId == target) { unchanged++; continue; }
            var before = p.CustomerId is { } oldId && byId.TryGetValue(oldId, out var old) ? old.Label : null;
            var after = target is { } newId && byId.TryGetValue(newId, out var neu) ? neu.Label : null;
            if (target is null)
            {
                f.Audit("CustomerCleared", nameof(Part), p.Id, $"Cleared the customer on part {p.PartNumber} (was {before ?? "a customer"}). Part ID and existing links kept.", p.SiteId,
                    context: new { before, after = (string?)null, partId = p.Id });
                cleared++;
            }
            else
            {
                var text = before is null
                    ? $"Assigned {after} to part {p.PartNumber}. Part ID and existing links kept."
                    : $"Assigned {after} to part {p.PartNumber} (was {before}). Part ID and existing links kept.";
                f.Audit("CustomerAssigned", nameof(Part), p.Id, text, p.SiteId, context: new { before, after, partId = p.Id });
                assigned++;
            }
            updates.Add((p.Id, target));
        }
        foreach (var group in updates.GroupBy(u => u.CustomerId))
        {
            var cid = group.Key;
            foreach (var chunk in group.Select(u => u.Id).Chunk(500))
            {
                var ids = chunk.ToList();
                await db.Parts.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, cid), ct);
            }
        }
        f.Audit("CustomerAssignmentConfirmed", nameof(Part), Guid.Empty,
            $"Confirmed customer assignment: {assigned} assigned, {unchanged} already set, {cleared} cleared. Part IDs and existing links kept.",
            context: new { assigned, unchanged, cleared });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CustomerAssignmentResult(assigned, unchanged, cleared);
    }

    async Task<Dictionary<Guid, string>> CustomerNamesAsync(CancellationToken ct) =>
        await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.Customers).ToDictionaryAsync(v => v.Id, v => v.Label, ct);

    async Task<Dictionary<Guid, Deps>> DepsAsync(List<Guid> ids, CancellationToken ct)
    {
        var map = ids.Distinct().ToDictionary(id => id, _ => new Deps());
        if (map.Count == 0) return map;
        foreach (var chunk in map.Keys.Chunk(400))
        {
            var idChunk = chunk.ToArray();
            var orders = await db.SequflohTrackingRecords.AsNoTracking().Where(w => w.PartId != null && idChunk.Contains(w.PartId.Value))
                .Select(w => new { PartId = w.PartId!.Value, w.Number, w.Type, w.Findings }).ToListAsync(ct);
            foreach (var g in orders.GroupBy(o => o.PartId))
            {
                var d = map[g.Key];
                d.WorkOrders += g.Count();
                d.Fai += g.Count(o => o.Type == TrackingType.Fai);
                d.Validation += g.Count(o => o.Type == TrackingType.Validation);
                d.Ncr += g.Count(o => !string.IsNullOrWhiteSpace(o.Findings) && o.Findings != "{}");
                var numbers = string.Join("; ", g.Select(o => o.Number).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                d.WorkOrderNumbers = string.IsNullOrEmpty(d.WorkOrderNumbers) ? (numbers.Length == 0 ? null : numbers) : d.WorkOrderNumbers + "; " + numbers;
            }
            foreach (var row in await db.SequflohInspectionRequests.AsNoTracking().Where(w => w.PartId != null && idChunk.Contains(w.PartId.Value)).GroupBy(w => w.PartId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].InspectionRequests += row.N;
            foreach (var row in await db.DrawingPartLinks.AsNoTracking().Where(l => idChunk.Contains(l.PartId)).GroupBy(l => l.PartId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].DrawingLinks += row.N;
            foreach (var row in await db.SequflohWorkInstructions.AsNoTracking().Where(w => w.PartId != null && idChunk.Contains(w.PartId.Value)).GroupBy(w => w.PartId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].WorkInstructions += row.N;
            foreach (var row in await db.SequflohPartsLists.AsNoTracking().Where(l => l.ParentPartId != null && idChunk.Contains(l.ParentPartId.Value)).GroupBy(l => l.ParentPartId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].PartsLists += row.N;
            foreach (var row in await db.SequflohPartsListItems.AsNoTracking().Where(l => l.PartId != null && idChunk.Contains(l.PartId.Value)).GroupBy(l => l.PartId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].PartsLists += row.N;
            foreach (var row in await db.PartComponentLinks.AsNoTracking().Where(l => idChunk.Contains(l.PartId)).GroupBy(l => l.PartId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].ComponentLinks += row.N;
            foreach (var row in await db.PartComponentLinks.AsNoTracking().Where(l => idChunk.Contains(l.ComponentPartId)).GroupBy(l => l.ComponentPartId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct))
                map[row.Key].ComponentLinks += row.N;
        }
        return map;
    }

    /// <summary>The Assigned tab includes a saved customer whose suggestion names someone else. Unassigned and Ambiguous are only parts with no saved customer.</summary>
    static bool Matches(string outcome, string want) =>
        want.Equals(Assigned, StringComparison.OrdinalIgnoreCase) ? outcome is Assigned or Different : outcome.Equals(want, StringComparison.OrdinalIgnoreCase);

    /// <summary>A CSV cell. A value a spreadsheet would run as a formula is prefixed with an apostrophe.</summary>
    static string Csv(string? v)
    {
        var s = v ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
