using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record DevelopmentInput(Guid? AssignedEngineerId = null, DateOnly? TargetCompletionDate = null, string? Project = null, string? EngineeringNotes = null, string? DevelopmentStatus = null);
public sealed record InstructionCreateInput(WorkInstructionType Type, string Title, Guid SiteId, Guid? DepartmentId, Guid? PartId, Guid? TemplateId = null, string? Number = null);
public sealed record InstructionSaveInput(string Title, Guid SiteId, Guid? DepartmentId, Guid? OwnerId, Guid? PartId, string? ProductFamily, string? Assembly,
    string? Model, string? Variant, string? Process, string? Operation, string? Workstation, string? Location, string? ValidationType, string? FaiType, string? RepairCategory, string? RepairType,
    string? RepairDescription, string? ResultStatus, DateOnly? EffectiveDate, JsonObject? Content, JsonArray? Steps, JsonArray? Characteristics, JsonArray? PartsRows, uint RowVersion = 0, JsonArray? Blocks = null);
public sealed record InstructionActionInput(string? Comment, DateOnly? EffectiveDate, uint RowVersion = 0);
public sealed record CopyStepsInput(List<Guid> OperationStepIds);
public sealed record CopyPartsListInput(Guid? PartsListId, List<Guid>? PartIds, bool Replace = false);
public sealed record CopyCharacteristicsInput(Guid FromInstructionId);

/// <summary>
/// SEQUFLOH work instruction engine: create, save, lifecycle transitions with append-only transition records, controlled revisions, duplication,
/// copying library data (operation steps, parts lists, characteristics) into the instruction, and release rules driven by the designer schema.
/// Every transition reloads and locks the row, so a stale or repeated click cannot apply twice.
/// </summary>
public sealed class SequflohInstructionService(AppDbContext db, ModuleSupport m, SequflohFoundationService f,
    SequflohNotifications notify, ICurrentUser me)
{
    public const string EntityType = nameof(SequflohWorkInstruction);
    public static readonly WorkInstructionStatus[] Editable = [WorkInstructionStatus.Draft];
    static string? T(string? s, int max) { if (string.IsNullOrWhiteSpace(s)) return null; s = s.Trim(); return s.Length > max ? throw new AppException($"A value is limited to {max} characters.") : s; }

    /// <summary>
    /// Lifecycle actions. A work instruction is a draft until someone allowed to release it presses Release; there is no review or approval step.
    /// Releasing a new revision supersedes the one before it. A draft or released instruction can be marked obsolete.
    /// </summary>
    public sealed record ActionRule(string Action, string Label, WorkInstructionStatus[] From, WorkInstructionStatus To, string Permission, bool CommentRequired);
    public static readonly IReadOnlyList<ActionRule> Actions =
    [
        new("release", "Release", [WorkInstructionStatus.Draft], WorkInstructionStatus.Released, Permissions.SequflohReleaseWorkInstruction, false),
        new("obsolete", "Mark obsolete", [WorkInstructionStatus.Draft, WorkInstructionStatus.Released, WorkInstructionStatus.Superseded], WorkInstructionStatus.Obsolete, Permissions.SequflohObsoleteWorkInstruction, true),
    ];

    // ------------------------------------------------------------------ queries
    public async Task<IQueryable<SequflohWorkInstruction>> QueryAsync(CancellationToken ct) => await m.ScopeAsync(db.SequflohWorkInstructions.AsNoTracking(), Permissions.SequflohAccess, ct);

    public async Task<SequflohWorkInstruction> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var q = track ? db.SequflohWorkInstructions : db.SequflohWorkInstructions.AsNoTracking();
        var wi = await q.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work instruction");
        await m.RequireAsync(Permissions.SequflohAccess, wi.SiteId, wi.DepartmentId, ct);
        return wi;
    }

    async Task<SequflohWorkInstruction> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM sqf.sequfloh_work_instructions WHERE \"Id\" = {id} FOR UPDATE", ct);
        return await LoadAsync(id, true, ct);
    }

    /// <summary>Actions the current user can take now (state and permission), for the designer toolbar.</summary>
    public async Task<List<object>> AvailableActionsAsync(SequflohWorkInstruction wi, CancellationToken ct)
    {
        var list = new List<object>();
        foreach (var a in Actions.Where(a => a.From.Contains(wi.Status)))
            if (await m.CanAsync(a.Permission, wi.SiteId, wi.DepartmentId, ct)) list.Add(new { a.Action, a.Label, a.CommentRequired });
        if (wi.Status == WorkInstructionStatus.Released && await m.CanAsync(Permissions.SequflohReviseWorkInstruction, wi.SiteId, wi.DepartmentId, ct)
            && !await InProgressRevisionAsync(wi, ct))
            list.Add(new { Action = "revise", Label = "Create revision", CommentRequired = true });
        return list;
    }

    static readonly WorkInstructionStatus[] Closed = [WorkInstructionStatus.Released, WorkInstructionStatus.Superseded, WorkInstructionStatus.Obsolete];
    Task<bool> InProgressRevisionAsync(SequflohWorkInstruction wi, CancellationToken ct) =>
        db.SequflohWorkInstructions.AnyAsync(x => x.FamilyId == wi.FamilyId && x.Id != wi.Id && !Closed.Contains(x.Status), ct);

    // ------------------------------------------------------------------ Procedure Instructions and type changes
    /// <summary>The newest draft or released Procedure Instruction with this title at the site, which a new import of the same file revises.</summary>
    public async Task<SequflohWorkInstruction?> ProcedureToReviseAsync(string title, Guid siteId, CancellationToken ct)
    {
        var t = title.Trim();
        var hit = await db.SequflohWorkInstructions.AsNoTracking()
            .Where(x => x.Type == WorkInstructionType.Procedure && x.SiteId == siteId && x.Title == t && (x.Status == WorkInstructionStatus.Draft || x.Status == WorkInstructionStatus.Released))
            .OrderByDescending(x => x.Status == WorkInstructionStatus.Draft).ThenByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (hit is not null) await m.RequireAsync(Permissions.SequflohCreateWorkInstruction, hit.SiteId, hit.DepartmentId, ct);
        return hit;
    }

    /// <summary>Archives the files of a draft before the new file of a re-import is attached, so the viewer shows the new file.</summary>
    public async Task ArchiveFilesAsync(Guid id, CancellationToken ct)
    {
        foreach (var a in await db.Attachments.Where(a => a.OwnerType == EntityType && a.OwnerId == id && !a.IsArchived).ToListAsync(ct)) a.IsArchived = true;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Changes the type of an instruction (any type to any type). The audit trail records who, when and the old and new type.</summary>
    public async Task ChangeTypeAsync(Guid id, WorkInstructionType type, CancellationToken ct)
    {
        if (!SequflohDesigner.Schemas.ContainsKey(type)) throw new AppException("Choose the work instruction type.");
        var wi = await LoadAsync(id, true, ct);
        await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, wi.SiteId, wi.DepartmentId, ct);
        if (wi.Type == type) return;
        var old = wi.Type;
        wi.Type = type;
        db.AuditEntries.Add(new Sequfloh.Domain.Audit.AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = m.UserId, UserName = me.UserName, Action = "Changed instruction type", EntityType = EntityType, EntityId = wi.Id, SiteId = wi.SiteId,
            CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Work instruction {wi.Number} rev {wi.Revision} type changed from {SequflohDesigner.Schemas[old].Title} to {SequflohDesigner.Schemas[type].Title}"),
        });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ create / save
    public async Task<SequflohWorkInstruction> CreateAsync(InstructionCreateInput r, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohCreateWorkInstruction, r.SiteId, r.DepartmentId, ct);
        await m.ValidateLocationAsync(r.SiteId, r.DepartmentId, ct);
        ModuleSupport.Required(r.Title, "Title", 300);
        if (!SequflohDesigner.Schemas.TryGetValue(r.Type, out var schema)) throw new AppException("Choose the work instruction type.");
        var number = RecordNumbers.Clean(r.Number, 200, "work instruction number");
        await RecordNumbers.EnsureFreeAsync(db.SequflohWorkInstructions.IgnoreQueryFilters().Select(o => o.Number), number, "A work instruction numbered", ct);
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = new SequflohWorkInstruction
        {
            Type = r.Type, Title = r.Title.Trim(), SiteId = r.SiteId, DepartmentId = r.DepartmentId, Status = WorkInstructionStatus.Draft, OwnerId = m.UserId, AuthorId = m.UserId,
            Revision = SequflohFoundationService.FirstRevision(settings.Numbering.RevisionFormat), Number = number
        };
        wi.FamilyId = wi.Id;
        await ApplyPartAsync(wi, r.PartId, ct);
        if (r.TemplateId is { } tid)
        {
            var t = await db.SequflohInstructionTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid && x.IsActive, ct) ?? throw new AppException("The template is not available.");
            if (t.Type is { } tt && tt != r.Type) throw new AppException($"The template '{t.Name}' is for another instruction type.");
            wi.Blocks = SequflohBlocks.Fresh(t.Blocks); wi.TemplateId = t.Id;
        }
        db.SequflohWorkInstructions.Add(wi);
        Transition(wi, "create", null, WorkInstructionStatus.Draft, null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(wi, ct);
        return wi;
    }

    async Task ApplyPartAsync(SequflohWorkInstruction wi, Guid? partId, CancellationToken ct)
    {
        if (partId == wi.PartId && partId is not null) return;
        wi.PartId = partId;
        if (partId is null) { wi.PartNumber = null; wi.PartDescription = null; return; }
        var p = await db.Parts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == partId, ct) ?? throw new AppException("The part does not exist in the Part Master.");
        if (p.Status != Domain.MasterData.MasterStatus.Active) throw new AppException($"Part {p.PartNumber} is not active.");
        wi.PartNumber = p.PartNumber; wi.PartDescription = p.Name; wi.ProductFamily ??= p.ProductFamily; wi.Assembly ??= p.AssemblyType;
    }

    public async Task SaveAsync(Guid id, InstructionSaveInput r, CancellationToken ct)
    {
        var wi = await LockAsync(id, ct);
        await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, wi.SiteId, wi.DepartmentId, ct);
        if (!Editable.Contains(wi.Status))
            throw new ConflictException(wi.Status == WorkInstructionStatus.Released ? "Released work instructions are read-only. Create a revision to change it." : $"The work instruction is {Words(wi.Status)} and cannot be edited.");
        if (r.RowVersion != 0 && r.RowVersion != wi.RowVersion) throw new ConflictException("Someone else saved this work instruction. Reload to see the latest version.");
        ModuleSupport.Required(r.Title, "Title", 300);
        if (r.SiteId != wi.SiteId || r.DepartmentId != wi.DepartmentId)
        {
            await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, r.SiteId, r.DepartmentId, ct);
            await m.ValidateLocationAsync(r.SiteId, r.DepartmentId, ct);
        }
        if (r.OwnerId is { } uid) await m.RequireActiveUserAsync(uid, "owner", ct);
        await RequireOptionsAsync(wi.Type, r, ct);
        wi.Title = r.Title.Trim(); wi.SiteId = r.SiteId; wi.DepartmentId = r.DepartmentId; wi.OwnerId = r.OwnerId;
        await ApplyPartAsync(wi, r.PartId, ct);
        wi.ProductFamily = T(r.ProductFamily, 100); wi.Assembly = T(r.Assembly, 100); wi.Model = T(r.Model, 100); wi.Variant = T(r.Variant, 100); wi.Process = T(r.Process, 200);
        wi.Operation = T(r.Operation, 200); wi.Workstation = T(r.Workstation, 100); wi.Location = T(r.Location, 200);
        wi.ValidationType = T(r.ValidationType, 100); wi.FaiType = T(r.FaiType, 100); wi.RepairCategory = T(r.RepairCategory, 100); wi.RepairType = T(r.RepairType, 100);
        wi.RepairDescription = T(r.RepairDescription, 200); wi.ResultStatus = T(r.ResultStatus, 50); wi.EffectiveDate = r.EffectiveDate;
        var schema = SequflohDesigner.Schemas[wi.Type];
        wi.Content = CleanContent(r.Content, schema);
        wi.Steps = CleanRows(r.Steps, schema.StepColumns, true, "step");
        wi.Characteristics = schema.HasCharacteristics ? CleanRows(r.Characteristics, schema.CharacteristicColumns, true, "characteristic") : "[]";
        wi.PartsRows = schema.HasPartsRows ? CleanRows(r.PartsRows, schema.PartsRowColumns, true, "parts row") : "[]";
        if (r.Blocks is not null)
        {
            wi.Blocks = SequflohBlocks.Clean(r.Blocks);
            // an image block may only show a file attached to this instruction or an earlier revision of it
            var images = SequflohBlocks.ImageIds(wi.Blocks);
            if (images.Count > 0)
            {
                var family = db.SequflohWorkInstructions.Where(x => x.FamilyId == wi.FamilyId).Select(x => x.Id);
                var ok = await db.Attachments.CountAsync(a => images.Contains(a.Id) && a.OwnerType == EntityType && family.Contains(a.OwnerId) && !a.IsArchived, ct);
                if (ok != images.Distinct().Count()) throw new AppException("An image block can only show a picture attached to this work instruction.");
            }
        }
        await db.SaveChangesAsync(ct);
        await IndexAsync(wi, ct);
    }

    async Task RequireOptionsAsync(WorkInstructionType type, InstructionSaveInput r, CancellationToken ct)
    {
        await f.RequireOptionAsync("validation-types", r.ValidationType, "Validation type", ct);
        await f.RequireOptionAsync("fai-types", r.FaiType, "FAI type", ct);
        await f.RequireOptionAsync("repair-categories", r.RepairCategory, "Repair category", ct);
        await f.RequireOptionAsync("repair-types", r.RepairType, "Repair type", ct);
        await f.RequireOptionAsync("repair-descriptions", r.RepairDescription, "Repair description", ct);
    }

    /// <summary>Keeps only the schema's sections and fields; values are strings, numbers, booleans or null; text is length-limited.</summary>
    static string CleanContent(JsonObject? content, DesignerSchema schema)
    {
        var o = new JsonObject();
        if (content is null) return "{}";
        foreach (var s in schema.Sections)
        {
            if (content[s.Key] is not JsonObject src) continue;
            var dst = new JsonObject();
            foreach (var fld in s.Fields) if (CleanValue(src[fld.Key], fld) is { } v) dst[fld.Key] = v;
            if (dst.Count > 0) o[s.Key] = dst;
        }
        return o.ToJsonString();
    }

    static JsonNode? CleanValue(JsonNode? n, DesignerField fld)
    {
        if (n is null) return null;
        if (n is not JsonValue v) throw new AppException($"{fld.Label} has an invalid value.");
        switch (fld.Kind)
        {
            case "bool": return v.TryGetValue<bool>(out var b) ? JsonValue.Create(b) : throw new AppException($"{fld.Label} must be yes or no.");
            case "number":
                if (v.TryGetValue<decimal>(out var d)) return JsonValue.Create(d);
                if (v.TryGetValue<string>(out var ds) && string.IsNullOrWhiteSpace(ds)) return null;
                if (v.TryGetValue<string>(out var dn) && decimal.TryParse(dn, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var dv)) return JsonValue.Create(dv);
                throw new AppException($"{fld.Label} must be a number.");
            case "date":
                if (!v.TryGetValue<string>(out var dt) || string.IsNullOrWhiteSpace(dt)) return null;
                return DateOnly.TryParse(dt, System.Globalization.CultureInfo.InvariantCulture, out _) ? JsonValue.Create(dt.Trim()) : throw new AppException($"{fld.Label} must be a date.");
            default:
                var s = v.ToString().Trim();
                if (s.Length == 0) return null;
                var max = fld.Kind == "multiline" ? 8000 : 500;
                return s.Length > max ? throw new AppException($"{fld.Label} is limited to {max} characters.") : JsonValue.Create(s);
        }
    }

    /// <summary>Rows keep an id, an explicit sequence number and the schema's columns; limits are checked (lower/minimum not above upper/maximum).</summary>
    static string CleanRows(JsonArray? rows, IReadOnlyList<DesignerField> cols, bool sequence, string what)
    {
        var a = new JsonArray();
        if (rows is null) return "[]";
        if (rows.Count > 500) throw new AppException($"A work instruction is limited to 500 {what}s.");
        var i = 0;
        foreach (var r in rows)
        {
            i++;
            if (r is not JsonObject src) throw new AppException($"Each {what} must be an object.");
            var dst = new JsonObject { ["id"] = src["id"] is JsonValue idv && idv.TryGetValue<string>(out var ids) && Guid.TryParse(ids, out var g) ? g.ToString() : Guid.NewGuid().ToString() };
            if (sequence)
            {
                var seq = src["sequence"] is JsonValue sv && sv.TryGetValue<int>(out var sn) ? sn : src["sequence"] is JsonValue ss && ss.TryGetValue<string>(out var st) && int.TryParse(st, out var sp) ? sp : i * 10;
                dst["sequence"] = seq;
            }
            if (src["sourceOperationStepId"] is JsonValue so && so.TryGetValue<string>(out var sos)) dst["sourceOperationStepId"] = sos;
            if (src["partId"] is JsonValue pv && pv.TryGetValue<string>(out var pid) && Guid.TryParse(pid, out _)) dst["partId"] = pid;
            foreach (var c in cols) if (CleanValue(src[c.Key], c) is { } v) dst[c.Key] = v;
            foreach (var (lo, hi) in new[] { ("lowerLimit", "upperLimit"), ("minimum", "maximum") })
                if (dst[lo] is JsonValue l && dst[hi] is JsonValue h && l.GetValue<decimal>() > h.GetValue<decimal>())
                    throw new AppException($"{Cap(what)} {(dst["sequence"]?.ToString() ?? i.ToString())}: the {Words(lo)} cannot be greater than the {Words(hi)}.");
            a.Add(dst);
        }
        return a.ToJsonString();
    }

    // ------------------------------------------------------------------ release rules
    /// <summary>Problems that block release (sequence order, required content per instruction type).</summary>
    public async Task<List<string>> ReleaseProblemsAsync(SequflohWorkInstruction wi, bool forRelease, CancellationToken ct)
    {
        var problems = new List<string>();
        var schema = SequflohDesigner.Schemas[wi.Type];
        var steps = JsonNode.Parse(wi.Steps)!.AsArray();
        var content = JsonNode.Parse(wi.Content)!.AsObject();
        string? C(string section, string field) => content[section]?[field]?.ToString() is { Length: > 0 } s ? s : null;
        var seqs = steps.Select(s => s?["sequence"]?.GetValue<int>() ?? 0).ToList();
        if (seqs.Any(s => s <= 0)) problems.Add("Every step needs a positive sequence number.");
        if (seqs.Distinct().Count() != seqs.Count) problems.Add("Step sequence numbers must be unique.");
        if (!seqs.SequenceEqual(seqs.OrderBy(x => x))) problems.Add("Steps must be in ascending sequence order.");
        foreach (var s in steps) if (string.IsNullOrWhiteSpace(s?["title"]?.ToString())) { problems.Add("Every step needs a title."); break; }
        switch (wi.Type)
        {
            case WorkInstructionType.Validation or WorkInstructionType.ValidationFai:
                if (steps.Count == 0) problems.Add("Add at least one validation step.");
                if (C("validationAcceptance", "overallAcceptanceCriteria") is null && C("acceptance", "acceptanceCriteria") is null) problems.Add("Enter the acceptance criteria.");
                break;
            case WorkInstructionType.RepairTest:
                if (steps.Count == 0) problems.Add("Add at least one test step.");
                if (C("repairTest", "passCriteria") is null) problems.Add("Enter the pass criteria.");
                break;
            case WorkInstructionType.Inspection:
                if (steps.Count == 0) problems.Add("Add at least one inspection step.");
                break;
            case WorkInstructionType.RepairWork:
                if (wi.PartId is null) problems.Add("Choose the part being repaired.");
                if (wi.RepairDescription is null) problems.Add("Choose the repair description.");
                if (steps.Count == 0) problems.Add("Add at least one operation step.");
                foreach (var p in JsonNode.Parse(wi.PartsRows)!.AsArray())
                    if (string.IsNullOrWhiteSpace(p?["replacementPartNumber"]?.ToString()) || (p?["quantity"]?.GetValue<decimal>() ?? 0) <= 0)
                    { problems.Add("Every parts row needs a replacement part number and a quantity greater than zero."); break; }
                break;
        }
        if (schema.HasCharacteristics)
        {
            var chars = JsonNode.Parse(wi.Characteristics)!.AsArray();
            if (chars.Count == 0) problems.Add("Add at least one FAI characteristic.");
            var nums = chars.Select(c => c?["characteristicNumber"]?.ToString()).ToList();
            if (nums.Any(string.IsNullOrWhiteSpace) || chars.Any(c => string.IsNullOrWhiteSpace(c?["requirement"]?.ToString()))) problems.Add("Every characteristic needs a characteristic number and a requirement.");
            else if (nums.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nums.Count) problems.Add("Characteristic numbers must be unique.");
        }
        if (forRelease)
        {
            var settings = await f.GetSettingsAsync(ct);
            if (settings.WorkInstructions.EffectiveDateRequired && wi.EffectiveDate is null) problems.Add("Enter the effective date before release.");
        }
        return problems;
    }

    // ------------------------------------------------------------------ transitions
    public async Task<SequflohWorkInstruction> ActAsync(Guid id, string action, InstructionActionInput r, CancellationToken ct)
    {
        var rule = Actions.FirstOrDefault(a => a.Action == action) ?? throw new NotFoundException("Action");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = await LockAsync(id, ct); // latest database state
        await m.RequireAsync(rule.Permission, wi.SiteId, wi.DepartmentId, ct);
        if (r.RowVersion != 0 && r.RowVersion != wi.RowVersion) throw new ConflictException("The work instruction changed since you opened it. Reload and try again.");
        if (!rule.From.Contains(wi.Status)) throw new ConflictException($"{rule.Label} is not possible while the work instruction is {Words(wi.Status)}.");
        var comment = T(r.Comment, 4000);
        if (rule.CommentRequired && comment is null) throw new AppException($"Enter a comment to {rule.Label.ToLowerInvariant()}.");
        var from = wi.Status;
        var to = rule.To;
        var superseded = new List<SequflohWorkInstruction>();
        switch (action)
        {
            case "release":
                if (r.EffectiveDate is not null && wi.EffectiveDate is null) wi.EffectiveDate = r.EffectiveDate;
                var rp = await ReleaseProblemsAsync(wi, true, ct);
                if (rp.Count > 0) throw new AppException(string.Join(" ", rp));
                wi.ReleasedAt = m.Now; wi.ReleasedById = m.UserId;
                // the previously released revision of this instruction is superseded by this release
                var prior = await db.SequflohWorkInstructions.Where(x => x.FamilyId == wi.FamilyId && x.Id != wi.Id && x.Status == WorkInstructionStatus.Released).ToListAsync(ct);
                superseded.AddRange(prior);
                foreach (var old in prior)
                {
                    old.Status = WorkInstructionStatus.Superseded; old.SupersededAt = m.Now;
                    Transition(old, "supersede", WorkInstructionStatus.Released, WorkInstructionStatus.Superseded, $"Superseded by revision {wi.Revision}.");
                }
                break;
            case "obsolete": wi.ObsoleteAt = m.Now; break;
        }
        wi.Status = to;
        Transition(wi, action, from, to, comment);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(wi, ct);
        await notify.InstructionChangedAsync(wi, action, superseded, ct);
        foreach (var old in superseded) await IndexAsync(old, ct);
        return wi;
    }

    // module search reads the records directly, so there is no separate index to keep up to date
    static Task IndexAsync(SequflohWorkInstruction _, CancellationToken __) => Task.CompletedTask;

    void Transition(SequflohWorkInstruction wi, string action, WorkInstructionStatus? from, WorkInstructionStatus to, string? comment)
    {
        db.SequflohWorkInstructionTransitions.Add(new SequflohWorkInstructionTransition { WorkInstructionId = wi.Id, Action = action, FromStatus = from, ToStatus = to, UserId = m.UserId, Comment = comment, Revision = wi.Revision });
        f.Audit(action == "create" ? "WorkInstructionCreated" : "WorkInstruction" + string.Concat(action.Split('-').Select(Cap)), EntityType, wi.Id,
            $"{wi.Number} rev {wi.Revision}: {(from is null ? "" : Words(from.Value) + " -> ")}{Words(to)}", wi.SiteId, comment);
    }

    // ------------------------------------------------------------------ revision / duplicate
    /// <summary>
    /// Engineering development record on an instruction that is not yet released: assigned engineer, target date, project, notes, development status. These are planning fields, not controlled content, so they do not need a revision; changes are audited.
    /// </summary>
    public async Task<SequflohWorkInstruction> SaveDevelopmentAsync(Guid id, DevelopmentInput r, CancellationToken ct)
    {
        var wi = await LoadAsync(id, true, ct);
        await m.RequireAsync(Permissions.SequflohEditEngineeringFolder, wi.SiteId, wi.DepartmentId, ct);
        if (wi.Status is WorkInstructionStatus.Released or WorkInstructionStatus.Superseded or WorkInstructionStatus.Obsolete)
            throw new ConflictException($"{wi.Number} rev {wi.Revision} is {Words(wi.Status.ToString()).ToLowerInvariant()}. Development details apply to instructions not yet released.");
        if (r.AssignedEngineerId is { } eng)
        {
            await m.RequireActiveUserAsync(eng, "assigned engineer", ct);
            if (!await m.UserCanAsync(eng, Permissions.SequflohEditEngineeringFolder, wi.SiteId, wi.DepartmentId, ct)) throw new AppException("The assigned engineer needs Engineering folder access at this site.");
        }
        string? T(string? v, int max) { if (string.IsNullOrWhiteSpace(v)) return null; v = v.Trim(); return v.Length > max ? throw new AppException($"A value is limited to {max} characters.") : v; }
        var changed = new List<string>();
        void Set<TV>(string name, TV? now, TV? value, Action<TV?> apply) { if (!EqualityComparer<TV?>.Default.Equals(now, value)) { apply(value); changed.Add(name); } }
        Set("assigned engineer", wi.AssignedEngineerId, r.AssignedEngineerId, v => wi.AssignedEngineerId = v);
        Set("target completion date", wi.TargetCompletionDate, r.TargetCompletionDate, v => wi.TargetCompletionDate = v);
        Set("project", wi.Project, T(r.Project, 100), v => wi.Project = v);
        Set("Engineering notes", wi.EngineeringNotes, T(r.EngineeringNotes, 4000), v => wi.EngineeringNotes = v);
        Set("development status", wi.DevelopmentStatus, T(r.DevelopmentStatus, 100), v => wi.DevelopmentStatus = v);
        if (changed.Count == 0) return wi;
        f.Audit("DevelopmentUpdated", EntityType, wi.Id, $"{wi.Number} rev {wi.Revision}: {string.Join(", ", changed)}", wi.SiteId);
        await db.SaveChangesAsync(ct);
        return wi;
    }

    public async Task<SequflohWorkInstruction> ReviseAsync(Guid id, ReviseInput r, CancellationToken ct)
    {
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var src = await LockAsync(id, ct);
        await m.RequireAsync(Permissions.SequflohReviseWorkInstruction, src.SiteId, src.DepartmentId, ct);
        if (src.Status != WorkInstructionStatus.Released) throw new ConflictException("Only a released work instruction can be revised.");
        if (await InProgressRevisionAsync(src, ct))
            throw new ConflictException("A newer revision of this work instruction is already in progress.");
        if (settings.WorkInstructions.RevisionReasonRequired && string.IsNullOrWhiteSpace(r.Reason)) throw new AppException("Enter the reason for the new revision.");
        var wi = Copy(src);
        wi.FamilyId = src.FamilyId; wi.PreviousRevisionId = src.Id; wi.Number = src.Number;
        wi.Revision = SequflohFoundationService.NextRevision(src.Revision, settings.Numbering.RevisionFormat);
        wi.RevisionReason = T(r.Reason, 2000);
        db.SequflohWorkInstructions.Add(wi);
        Transition(wi, "create-revision", null, WorkInstructionStatus.Draft, wi.RevisionReason);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(wi, ct);
        return wi;
    }

    public async Task<SequflohWorkInstruction> DuplicateAsync(Guid id, string? number, CancellationToken ct)
    {
        var src = await LoadAsync(id, false, ct);
        await m.RequireAsync(Permissions.SequflohCreateWorkInstruction, src.SiteId, src.DepartmentId, ct);
        var n = RecordNumbers.Clean(number, 200, "work instruction number");
        await RecordNumbers.EnsureFreeAsync(db.SequflohWorkInstructions.IgnoreQueryFilters().Select(o => o.Number), n, "A work instruction numbered", ct);
        var settings = await f.GetSettingsAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = Copy(src);
        wi.Blocks = SequflohBlocks.WithoutImages(wi.Blocks); // a duplicate is a new instruction; its pictures are attached again
        wi.FamilyId = wi.Id; wi.Title = (src.Title + " (copy)")[..Math.Min(300, src.Title.Length + 7)];
        wi.Number = n;
        wi.Revision = SequflohFoundationService.FirstRevision(settings.Numbering.RevisionFormat);
        db.SequflohWorkInstructions.Add(wi);
        Transition(wi, "duplicate", null, WorkInstructionStatus.Draft, $"Copied from {src.Number} rev {src.Revision}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await IndexAsync(wi, ct);
        return wi;
    }

    SequflohWorkInstruction Copy(SequflohWorkInstruction s) => new()
    {
        Type = s.Type, Title = s.Title, Status = WorkInstructionStatus.Draft, SiteId = s.SiteId, DepartmentId = s.DepartmentId, OwnerId = s.OwnerId, AuthorId = m.UserId,
        PartId = s.PartId, PartNumber = s.PartNumber, PartDescription = s.PartDescription, ProductFamily = s.ProductFamily, Assembly = s.Assembly, Model = s.Model,
        Variant = s.Variant, Process = s.Process, Operation = s.Operation, Workstation = s.Workstation, Location = s.Location, ValidationType = s.ValidationType, FaiType = s.FaiType,
        RepairCategory = s.RepairCategory, RepairType = s.RepairType, RepairDescription = s.RepairDescription, SourcePartsListId = s.SourcePartsListId,
        Content = s.Content, Steps = NewIds(s.Steps), Characteristics = NewIds(s.Characteristics), PartsRows = NewIds(s.PartsRows),
        Blocks = SequflohBlocks.Fresh(s.Blocks), TemplateId = s.TemplateId
    };

    /// <summary>Adds a template's blocks to an editable instruction, after its own blocks or in place of them.</summary>
    public async Task<SequflohWorkInstruction> ApplyTemplateAsync(Guid id, ApplyTemplateInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = await EditableAsync(id, ct);
        var t = await db.SequflohInstructionTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.TemplateId && x.IsActive, ct) ?? throw new AppException("The template is not available.");
        if (t.Type is { } tt && tt != wi.Type) throw new AppException($"The template '{t.Name}' is for another instruction type.");
        var blocks = r.Replace ? new JsonArray() : JsonNode.Parse(wi.Blocks) as JsonArray ?? [];
        foreach (var b in (JsonNode.Parse(SequflohBlocks.Fresh(t.Blocks)) as JsonArray ?? []).ToList()) { b!.Parent?.AsArray().Remove(b); blocks.Add(b); }
        wi.Blocks = SequflohBlocks.Clean(blocks);
        wi.TemplateId = t.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return wi;
    }
    static string NewIds(string json)
    {
        var a = JsonNode.Parse(json)!.AsArray();
        foreach (var r in a) if (r is JsonObject o) o["id"] = Guid.NewGuid().ToString();
        return a.ToJsonString();
    }

    // ------------------------------------------------------------------ copy library data into the instruction (never linked back)
    async Task<SequflohWorkInstruction> EditableAsync(Guid id, CancellationToken ct)
    {
        var wi = await LockAsync(id, ct);
        await m.RequireAsync(Permissions.SequflohEditDraftWorkInstruction, wi.SiteId, wi.DepartmentId, ct);
        if (!Editable.Contains(wi.Status)) throw new ConflictException("The work instruction cannot be edited in its current status.");
        return wi;
    }

    public async Task<int> CopyOperationStepsAsync(Guid id, CopyStepsInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = await EditableAsync(id, ct);
        var templates = await db.SequflohOperationSteps.AsNoTracking().Where(x => r.OperationStepIds.Contains(x.Id) && x.IsActive).ToListAsync(ct);
        if (templates.Count != r.OperationStepIds.Distinct().Count()) throw new AppException("Choose active operation steps from the library.");
        var steps = JsonNode.Parse(wi.Steps)!.AsArray();
        var next = steps.Select(s => s?["sequence"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max();
        foreach (var t in r.OperationStepIds.Select(i => templates.First(x => x.Id == i)))
        {
            next = (next / 10 + 1) * 10;
            var o = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["sequence"] = next, ["sourceOperationStepId"] = t.Id.ToString(), ["title"] = t.Title };
            void Put(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) o[k] = v; }
            void Num(string k, decimal? v) { if (v is not null) o[k] = v; }
            Put("specification", t.Specification); Num("target", t.Target); Put("unit", t.Unit); Put("expectedResult", t.ExpectedResult); Put("evidenceRequirement", t.EvidenceRequirement); Put("qualityCheckpoint", t.QualityCheckpoint);
            switch (wi.Type)
            {
                case WorkInstructionType.RepairWork:
                    Put("operationStep", $"{t.Number} {t.Title}"); Put("detailedRepairAction", t.DetailedAction); Put("toolsRequired", t.DefaultTools); Put("partsConsumed", t.DefaultParts);
                    Num("minimum", t.Minimum); Num("maximum", t.Maximum); Put("warning", t.SafetyWarning); break;
                case WorkInstructionType.RepairTest:
                    Put("detailedAction", t.DetailedAction); Put("equipment", t.DefaultTools); Num("minimum", t.Minimum); Num("maximum", t.Maximum); break;
                case WorkInstructionType.Fai:
                    Put("detailedInstruction", t.DetailedAction); Put("tool", t.DefaultTools); Num("lowerLimit", t.Minimum); Num("upperLimit", t.Maximum); Put("safetyWarning", t.SafetyWarning); break;
                default:
                    Put("detailedAction", t.DetailedAction); Put("equipment", t.DefaultTools); Num("lowerLimit", t.Minimum); Num("upperLimit", t.Maximum); Put("responsibleRole", t.RequiredRole); break;
            }
            steps.Add(o);
        }
        wi.Steps = steps.ToJsonString();
        f.Audit("OperationStepsCopied", EntityType, wi.Id, $"{templates.Count} operation step(s) copied into {wi.Number} rev {wi.Revision}", wi.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return templates.Count;
    }

    public async Task<int> CopyPartsAsync(Guid id, CopyPartsListInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = await EditableAsync(id, ct);
        if (!SequflohDesigner.Schemas[wi.Type].HasPartsRows) throw new AppException("This instruction type has no parts list.");
        var rows = r.Replace ? new JsonArray() : JsonNode.Parse(wi.PartsRows)!.AsArray();
        var added = 0;
        if (r.PartsListId is { } lid)
        {
            var list = await db.SequflohPartsLists.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == lid, ct) ?? throw new AppException("The parts list does not exist.");
            if (list.Status == PartsListStatus.Inactive) throw new AppException("The parts list is inactive.");
            foreach (var i in list.Items.OrderBy(x => x.ItemNumber))
            {
                rows.Add(Row(i.PartId, i.PartNumber, i.Description, i.Quantity, i.Unit, i.Required, i.AlternatePartNumber,
                    i.Traceability is TraceabilityRequirement.Lot or TraceabilityRequirement.LotAndSerial, i.Traceability is TraceabilityRequirement.Serial or TraceabilityRequirement.LotAndSerial));
                added++;
            }
            wi.SourcePartsListId = lid;
        }
        if (r.PartIds is { Count: > 0 })
        {
            var parts = await db.Parts.AsNoTracking().Where(p => r.PartIds.Contains(p.Id)).ToListAsync(ct);
            foreach (var p in parts) { rows.Add(Row(p.Id, p.PartNumber, p.Name, 1, p.UnitOfMeasure, true, null, p.IsLotControlled, p.IsSerialized)); added++; }
        }
        wi.PartsRows = rows.ToJsonString();
        f.Audit("PartsCopied", EntityType, wi.Id, $"{added} parts row(s) copied into {wi.Number} rev {wi.Revision}", wi.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return added;
    }
    static JsonObject Row(Guid? partId, string pn, string? desc, decimal qty, string? unit, bool required, string? alt, bool lot, bool serial)
    {
        var o = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["replacementPartNumber"] = pn, ["quantity"] = qty, ["required"] = required, ["lotNumberRequired"] = lot, ["serialNumberRequired"] = serial };
        if (partId is not null) o["partId"] = partId.ToString();
        if (!string.IsNullOrWhiteSpace(desc)) o["description"] = desc;
        if (!string.IsNullOrWhiteSpace(unit)) o["unit"] = unit;
        if (!string.IsNullOrWhiteSpace(alt)) o["approvedAlternate"] = alt;
        return o;
    }

    public async Task<int> CopyCharacteristicsAsync(Guid id, CopyCharacteristicsInput r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wi = await EditableAsync(id, ct);
        if (!SequflohDesigner.Schemas[wi.Type].HasCharacteristics) throw new AppException("This instruction type has no characteristics.");
        var src = await LoadAsync(r.FromInstructionId, false, ct);
        var from = JsonNode.Parse(NewIds(src.Characteristics))!.AsArray();
        var mine = JsonNode.Parse(wi.Characteristics)!.AsArray();
        foreach (var c in from.ToList()) { from.Remove(c); mine.Add(c); }
        wi.Characteristics = mine.ToJsonString();
        var n = JsonNode.Parse(src.Characteristics)!.AsArray().Count;
        f.Audit("CharacteristicsCopied", EntityType, wi.Id, $"{n} characteristic(s) copied from {src.Number} rev {src.Revision}", wi.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return n;
    }

    public static string Words(WorkInstructionStatus s) => Words(s.ToString());
    public static string Words(string s) => System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
