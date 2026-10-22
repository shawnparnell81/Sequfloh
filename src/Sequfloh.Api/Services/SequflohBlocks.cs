using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Work instruction blocks. Every block has an id and a kind; the rest depends on the kind:
/// <list type="bullet">
/// <item><c>text</c>: title, body.</item>
/// <item><c>image</c>: caption, alt text and the id of an attachment on the instruction (or the template), for a photo, drawing or visual aid.</item>
/// <item><c>table</c>: title, column headings and rows of cells.</item>
/// <item><c>step</c>: operation number, title, body, and optional tools and expected result.</item>
/// <item><c>caution</c>: level (note, caution or warning) and body.</item>
/// <item><c>check</c>: label, kind (check or sign-off), who signs (a description, not a role in the system) and whether it is required.</item>
/// </list>
/// The order of the array is the order on the page.
/// </summary>
public static class SequflohBlocks
{
    public static readonly string[] Kinds = ["text", "image", "table", "step", "caution", "check"];
    public const int MaxBlocks = 300;

    static string? Str(JsonObject o, string key, int max, string label, bool required = false)
    {
        var s = o[key] is JsonValue v ? v.ToString().Trim() : null;
        if (string.IsNullOrEmpty(s)) return required ? throw new AppException($"{label} is required.") : null;
        return s.Length > max ? throw new AppException($"{label} is limited to {max} characters.") : s;
    }

    /// <summary>Validates and normalizes blocks; unknown keys are dropped, ids are kept or assigned.</summary>
    public static string Clean(JsonArray? blocks)
    {
        var a = new JsonArray();
        if (blocks is null) return "[]";
        if (blocks.Count > MaxBlocks) throw new AppException($"A work instruction is limited to {MaxBlocks} blocks.");
        var n = 0;
        foreach (var node in blocks)
        {
            n++;
            if (node is not JsonObject b) throw new AppException("Each block must be an object.");
            var kind = Str(b, "kind", 20, $"Block {n} kind", true)!;
            if (!Kinds.Contains(kind)) throw new AppException($"Block {n}: the kind must be one of {string.Join(", ", Kinds)}.");
            var id = b["id"] is JsonValue iv && Guid.TryParse(iv.ToString(), out var g) ? g : Guid.NewGuid();
            var o = new JsonObject { ["id"] = id.ToString(), ["kind"] = kind };
            void Put(string key, string? v) { if (v is not null) o[key] = v; }
            var at = $"Block {n}";
            switch (kind)
            {
                case "text":
                    Put("title", Str(b, "title", 300, $"{at} title"));
                    Put("body", Str(b, "body", 8000, $"{at} text", true));
                    break;
                case "image":
                    Put("caption", Str(b, "caption", 300, $"{at} caption"));
                    Put("alt", Str(b, "alt", 300, $"{at} description"));
                    if (Str(b, "attachmentId", 40, $"{at} image") is { } att)
                        o["attachmentId"] = Guid.TryParse(att, out var ag) ? ag.ToString() : throw new AppException($"{at}: choose an image attached to the instruction.");
                    break;
                case "table":
                    {
                        Put("title", Str(b, "title", 300, $"{at} title"));
                        var cols = (b["columns"] as JsonArray ?? []).Select((c, i) => c is JsonValue cv ? cv.ToString().Trim() : throw new AppException($"{at}: column {i + 1} must be text.")).ToList();
                        if (cols.Count is < 1 or > 12) throw new AppException($"{at}: a table has 1 to 12 columns.");
                        if (cols.Any(c => c.Length is 0 or > 100)) throw new AppException($"{at}: each column needs a heading of up to 100 characters.");
                        var rows = b["rows"] as JsonArray ?? [];
                        if (rows.Count > 200) throw new AppException($"{at}: a table is limited to 200 rows.");
                        var outRows = new JsonArray();
                        foreach (var r in rows)
                        {
                            var cells = r as JsonArray ?? throw new AppException($"{at}: each row must be a list of cells.");
                            var row = new JsonArray();
                            for (var i = 0; i < cols.Count; i++)
                            {
                                var cell = i < cells.Count && cells[i] is JsonValue cv ? cv.ToString() : "";
                                if (cell.Length > 1000) throw new AppException($"{at}: a cell is limited to 1000 characters.");
                                row.Add(cell);
                            }
                            outRows.Add(row);
                        }
                        o["columns"] = new JsonArray(cols.Select(c => (JsonNode)c).ToArray());
                        o["rows"] = outRows;
                        break;
                    }
                case "step":
                    Put("operation", Str(b, "operation", 40, $"{at} operation number", true));
                    Put("title", Str(b, "title", 300, $"{at} title", true));
                    Put("body", Str(b, "body", 8000, $"{at} instructions"));
                    Put("tools", Str(b, "tools", 1000, $"{at} tools"));
                    Put("expected", Str(b, "expected", 1000, $"{at} expected result"));
                    break;
                case "caution":
                    {
                        var level = Str(b, "level", 20, $"{at} level") ?? "caution";
                        if (level is not ("note" or "caution" or "warning")) throw new AppException($"{at}: the level must be note, caution or warning.");
                        o["level"] = level;
                        Put("body", Str(b, "body", 4000, $"{at} text", true));
                        break;
                    }
                case "check":
                    {
                        Put("label", Str(b, "label", 500, $"{at} check", true));
                        var ck = Str(b, "check", 20, $"{at} type") ?? "check";
                        if (ck is not ("check" or "signoff")) throw new AppException($"{at}: the type must be check or sign-off.");
                        o["check"] = ck;
                        Put("signer", Str(b, "signer", 200, $"{at} signed by"));
                        o["required"] = b["required"] is JsonValue rv && rv.TryGetValue<bool>(out var req) && req;
                        break;
                    }
            }
            a.Add(o);
        }
        var stepOps = a.OfType<JsonObject>().Where(x => (string?)x["kind"] == "step").Select(x => (string?)x["operation"]).ToList();
        if (stepOps.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1) is { } dup) throw new AppException($"Operation {dup.Key} is used by more than one step.");
        return a.ToJsonString();
    }

    /// <summary>Attachment ids that image blocks point at.</summary>
    public static List<Guid> ImageIds(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>().Select(b => b["attachmentId"] is JsonValue v && Guid.TryParse(v.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    /// <summary>Removes the picture from image blocks, keeping the caption and description as a placeholder.</summary>
    public static string WithoutImages(string json)
    {
        var a = JsonNode.Parse(json) as JsonArray ?? [];
        foreach (var b in a.OfType<JsonObject>()) b.Remove("attachmentId");
        return a.ToJsonString();
    }

    /// <summary>Copies blocks with new ids so a template and the instructions made from it never share block ids.</summary>
    public static string Fresh(string json)
    {
        var a = JsonNode.Parse(json) as JsonArray ?? [];
        foreach (var b in a.OfType<JsonObject>()) b["id"] = Guid.NewGuid().ToString();
        return a.ToJsonString();
    }
}

public sealed record InstructionTemplateInput(string Name, string? Description, WorkInstructionType? Type, JsonArray? Blocks, uint RowVersion = 0);
public sealed record SaveAsTemplateInput(string Name, string? Description, bool ForThisTypeOnly = true);
public sealed record ApplyTemplateInput(Guid TemplateId, bool Replace = false);

/// <summary>
/// Reusable work instruction templates. Anyone who may create work instructions may save a template; its owner or a person who manages
/// Sequfloh settings may change or retire it. Permissions come from the permission catalogue; no role names are checked.
/// </summary>
public sealed class SequflohTemplateService(AppDbContext db, ModuleSupport m)
{
    public async Task<List<SequflohInstructionTemplate>> ListAsync(WorkInstructionType? type, bool includeRetired, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohAccess, ct)) throw new ForbiddenException();
        var q = db.SequflohInstructionTemplates.AsNoTracking().Where(t => includeRetired || t.IsActive);
        if (type is not null) q = q.Where(t => t.Type == null || t.Type == type);
        return await q.OrderBy(t => t.Name).ToListAsync(ct);
    }

    public async Task<SequflohInstructionTemplate> GetAsync(Guid id, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohAccess, ct)) throw new ForbiddenException();
        return await db.SequflohInstructionTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Work instruction template");
    }

    public async Task<SequflohInstructionTemplate> CreateAsync(InstructionTemplateInput r, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohCreateWorkInstruction, ct)) throw new ForbiddenException();
        var t = new SequflohInstructionTemplate { OwnerId = m.UserId };
        await ApplyAsync(t, r, ct);
        db.SequflohInstructionTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<SequflohInstructionTemplate> UpdateAsync(Guid id, InstructionTemplateInput r, CancellationToken ct)
    {
        var t = await ManageableAsync(id, ct);
        if (r.RowVersion != 0 && r.RowVersion != t.RowVersion) throw new ConflictException("Someone else saved this template. Reload to see the latest version.");
        await ApplyAsync(t, r, ct);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        var t = await ManageableAsync(id, ct);
        t.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Saves the blocks of a work instruction as a new template.</summary>
    public async Task<SequflohInstructionTemplate> SaveFromInstructionAsync(Guid instructionId, SaveAsTemplateInput r, SequflohInstructionService instructions, CancellationToken ct)
    {
        var wi = await instructions.LoadAsync(instructionId, false, ct);
        await m.RequireAsync(Permissions.SequflohCreateWorkInstruction, wi.SiteId, wi.DepartmentId, ct);
        if (JsonNode.Parse(wi.Blocks) is not JsonArray { Count: > 0 } blocks) throw new AppException("Add blocks to the work instruction before saving it as a template.");
        foreach (var b in blocks.OfType<JsonObject>()) b.Remove("attachmentId"); // images belong to the instruction; the template keeps the placeholder
        return await CreateAsync(new InstructionTemplateInput(r.Name, r.Description, r.ForThisTypeOnly ? wi.Type : null, blocks), ct);
    }

    async Task ApplyAsync(SequflohInstructionTemplate t, InstructionTemplateInput r, CancellationToken ct)
    {
        ModuleSupport.Required(r.Name, "Name", 200);
        var name = r.Name.Trim();
        if (await db.SequflohInstructionTemplates.AnyAsync(x => x.Name == name && x.Id != t.Id, ct)) throw new ConflictException($"A template named '{name}' already exists.");
        if (r.Description is { Length: > 2000 }) throw new AppException("The description is limited to 2000 characters.");
        t.Name = name; t.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); t.Type = r.Type;
        t.Blocks = SequflohBlocks.WithoutImages(SequflohBlocks.Clean(r.Blocks)); // pictures stay with the instruction they were attached to
    }

    async Task<SequflohInstructionTemplate> ManageableAsync(Guid id, CancellationToken ct)
    {
        var t = await db.SequflohInstructionTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work instruction template");
        var mine = t.OwnerId == m.UserId && await m.CanAnyAsync(Permissions.SequflohCreateWorkInstruction, ct);
        if (!mine && !await m.CanAnyAsync(Permissions.SequflohManageSettings, ct)) throw new ForbiddenException();
        return t;
    }
}
