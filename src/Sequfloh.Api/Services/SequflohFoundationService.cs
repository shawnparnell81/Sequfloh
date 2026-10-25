using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Common;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record SettingsDto(GeneralSettings General, NumberingSettings Numbering, WorkInstructionSettings WorkInstructions, TrackingSettings Tracking, ValidationSettings Validation,
    FaiSettings Fai, RepairSettings Repair, DisplaySettings Display, ExportSettings Export, NotificationSettings Notifications, SecuritySettings Security, uint RowVersion);
public sealed record ReferenceValueInput(string Code, string Label, string? Description, bool IsActive = true, bool IsDefault = false, uint RowVersion = 0);
public sealed record ReorderInput(List<Guid> Ids);
public sealed record RoleAccessInput(List<string> Permissions, uint? RowVersion);
public sealed record UserItemInput(string Kind, string ItemType, Guid? RecordId, string Path, string Label);

/// <summary>
/// SEQUFLOH foundation: settings (inside the shared audited settings model), the revision format,
/// reference lists, module access on the existing roles, recent and pinned items, idempotent seeding and module audit events.
/// </summary>
public sealed class SequflohFoundationService(AppDbContext db, ModuleSupport m, ICurrentUser current, IAccessService access)
{
    public static readonly string[] Sections = ["general", "numbering", "workInstructions", "tracking", "validation", "fai", "repair", "display", "export", "notifications", "security"];

    // ------------------------------------------------------------------ audit helper
    /// <summary>Writes a SEQUFLOH audit event to the shared audit trail (append-only). Record changes on audited entities are written by the audit interceptor.</summary>
    public void Audit(string action, string entityType, Guid entityId, string description, Guid? siteId = null, string? reason = null, object? context = null)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = current.UserId, UserName = current.UserName, Action = action, EntityType = entityType, EntityId = entityId,
            Description = description.Length > 2000 ? description[..2000] : description, SiteId = siteId, Reason = reason, CorrelationId = current.CorrelationId,
            Changes = context is null ? null : JsonSerializer.Serialize(new Dictionary<string, object?> { ["module"] = SequflohCatalog.Module, ["context"] = context }, SequflohJson.Options)
        });
    }

    public async Task RecordAccessAsync(string page, CancellationToken ct)
    {
        Audit("ModuleAccessed", "SequflohModule", Guid.Empty, $"Opened SEQUFLOH {page}".Trim(), context: new { page });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ settings
    async Task<SequflohSettings> RowAsync(CancellationToken ct) =>
        await db.SequflohSettings.FirstOrDefaultAsync(ct) ?? throw new AppException("SEQUFLOH has not been initialized. Run the SEQUFLOH seed from Application Configuration.", 409);

    public async Task<SettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var s = await db.SequflohSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null) { await EnsureSeededAsync(ct); s = await db.SequflohSettings.AsNoTracking().FirstAsync(ct); }
        return ToDto(s);
    }

    public static SettingsDto ToDto(SequflohSettings s) => new(SequflohJson.Read<GeneralSettings>(s.General), SequflohJson.Read<NumberingSettings>(s.Numbering),
        SequflohJson.Read<WorkInstructionSettings>(s.WorkInstructions), SequflohJson.Read<TrackingSettings>(s.Tracking), SequflohJson.Read<ValidationSettings>(s.Validation),
        SequflohJson.Read<FaiSettings>(s.Fai), SequflohJson.Read<RepairSettings>(s.Repair), SequflohJson.Read<DisplaySettings>(s.Display), SequflohJson.Read<ExportSettings>(s.Export),
        SequflohJson.Read<NotificationSettings>(s.Notifications), SequflohJson.Read<SecuritySettings>(s.Security), s.RowVersion);

    /// <summary>Saves one settings area. The caller holds sequfloh.manage_settings (checked again here before the write).</summary>
    public async Task SaveSectionAsync(string section, JsonElement body, uint rowVersion, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageSettings, ct);
        var s = await RowAsync(ct);
        if (rowVersion != 0) db.Entry(s).Property(x => x.RowVersion).OriginalValue = rowVersion;
        var raw = body.GetRawText();
        T Parse<T>() where T : new()
        {
            try { return JsonSerializer.Deserialize<T>(raw, SequflohJson.Options) ?? throw new AppException("The settings are empty."); }
            catch (JsonException) { throw new AppException("The settings are not valid."); }
        }
        switch (section)
        {
            case "general":
                var g = Parse<GeneralSettings>();
                ModuleSupport.Required(g.DisplayName, "Module display name", 60);
                if (g.Description.Length > 500) throw new AppException("The module description is limited to 500 characters.");
                if (g.RecordsPerPage is < 5 or > 200) throw new AppException("Records per page must be between 5 and 200.");
                if (g.RecentItemsLimit is < 0 or > 50) throw new AppException("The recent-items limit must be between 0 and 50.");
                if (!g.DefaultLandingPage.StartsWith("/sequfloh", StringComparison.Ordinal)) throw new AppException("The default landing page must be a SEQUFLOH page.");
                if (g.DefaultSiteId is { } site) await m.ValidateLocationAsync(site, g.DefaultDepartmentId, ct);
                else if (g.DefaultDepartmentId is not null) throw new AppException("Select the default site of the default department.");
                s.General = SequflohJson.Write(g); break;
            case "numbering":
                var n = Parse<NumberingSettings>();
                if (n.RevisionFormat is not ("Letters" or "TwoDigits")) throw new AppException("The revision format must be Letters (A, B, C) or TwoDigits (00, 01, 02).");
                s.Numbering = SequflohJson.Write(n); break;
            case "workInstructions":
                var w = Parse<WorkInstructionSettings>();
                if (w.ObsoleteRetentionYears is < 0 or > 100) throw new AppException("Obsolete-document retention must be between 0 and 100 years.");
                if (w.PrintFooter.Length > 500) throw new AppException("The print footer is too long.");
                s.WorkInstructions = SequflohJson.Write(w); break;
            case "tracking":
                var t = Parse<TrackingSettings>();
                if (t.DefaultMinimumMinutes < 0 || t.DefaultMaximumMinutes < t.DefaultMinimumMinutes) throw new AppException("The default maximum time cannot be less than the default minimum time.");
                if (t.VarianceWarningPercent < 0 || t.VarianceCriticalPercent < t.VarianceWarningPercent) throw new AppException("The critical variance threshold cannot be below the warning threshold.");
                if (t.OverdueThresholdHours < 0 || t.ApproachingDueHours < 0) throw new AppException("Thresholds cannot be negative.");
                s.Tracking = SequflohJson.Write(t); break;
            case "validation": s.Validation = SequflohJson.Write(Parse<ValidationSettings>()); break;
            case "fai": s.Fai = SequflohJson.Write(Parse<FaiSettings>()); break;
            case "repair": s.Repair = SequflohJson.Write(Parse<RepairSettings>()); break;
            case "display": s.Display = SequflohJson.Write(Parse<DisplaySettings>()); break;
            case "export":
                var e = Parse<ExportSettings>();
                ModuleSupport.Required(e.FilenamePattern, "Filename pattern", 120);
                if (e.FilenamePattern.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '{' && c != '}').ToArray()) >= 0) throw new AppException("The filename pattern contains characters that are not allowed in file names.");
                try { _ = DateTime.UtcNow.ToString(e.DateFormat); } catch (FormatException) { throw new AppException("The export date format is not valid."); }
                s.Export = SequflohJson.Write(e); break;
            case "notifications": s.Notifications = SequflohJson.Write(Parse<NotificationSettings>()); break;
            case "security":
                var sec = Parse<SecuritySettings>();
                if (sec.RoleSuggestions.Any(r => string.IsNullOrWhiteSpace(r.Name) || r.Permissions.Any(p => !SequflohCatalog.AllPermissions.Contains(p))))
                    throw new AppException("Each suggested permission set needs a name and may only contain SEQUFLOH permissions.");
                s.Security = SequflohJson.Write(sec); break;
            default: throw new NotFoundException("Settings area");
        }
        await db.SaveChangesAsync(ct); // audited by the interceptor with the changed settings area
    }

    public async Task<bool> ModuleEnabledAsync(CancellationToken ct)
    {
        var s = await db.SequflohSettings.AsNoTracking().Select(x => x.General).FirstOrDefaultAsync(ct);
        return SequflohJson.Read<GeneralSettings>(s).ModuleEnabled;
    }

    /// <summary>First revision label in the configured format.</summary>
    public static string FirstRevision(string format) => format == "TwoDigits" ? "00" : "A";

    /// <summary>Next revision label: A → B … Z → AA; 00 → 01 … 99 → 100. Never reuses an existing label.</summary>
    public static string NextRevision(string current, string format)
    {
        if (format == "TwoDigits" || current.All(char.IsDigit))
            return int.TryParse(current, out var n) ? (n + 1).ToString().PadLeft(2, '0') : "00";
        var chars = current.ToUpperInvariant().ToCharArray();
        if (chars.Length == 0 || chars.Any(c => c is < 'A' or > 'Z')) return "A";
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] < 'Z') { chars[i]++; return new string(chars); }
            chars[i] = 'A';
        }
        return "A" + new string(chars);
    }

    // ------------------------------------------------------------------ reference data
    public async Task<object> ListsAsync(CancellationToken ct)
    {
        var counts = await db.SequflohReferenceValues.AsNoTracking().GroupBy(x => x.ListKey).Select(g => new { g.Key, Total = g.Count(), Active = g.Count(x => x.IsActive) }).ToListAsync(ct);
        return SequflohCatalog.Lists.Select(l => new { l.Key, l.Label, l.Description, Total = counts.FirstOrDefault(c => c.Key == l.Key)?.Total ?? 0, Active = counts.FirstOrDefault(c => c.Key == l.Key)?.Active ?? 0 });
    }

    static ReferenceListDef Def(string key) => SequflohCatalog.Lists.FirstOrDefault(l => l.Key == key) ?? throw new NotFoundException("Reference list");

    public async Task<List<SequflohReferenceValue>> ValuesAsync(string key, string? q, bool? active, CancellationToken ct)
    {
        Def(key);
        var query = db.SequflohReferenceValues.AsNoTracking().Where(x => x.ListKey == key);
        if (active is not null) query = query.Where(x => x.IsActive == active);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => EF.Functions.ILike(x.Code + " " + x.Label + " " + (x.Description ?? ""), $"%{q.Trim()}%"));
        return await query.OrderBy(x => x.SortOrder).ThenBy(x => x.Label).ToListAsync(ct);
    }

    /// <summary>Active values of a list for dropdowns (default first).</summary>
    public async Task<List<string>> OptionsAsync(string key, CancellationToken ct) =>
        await db.SequflohReferenceValues.AsNoTracking().Where(x => x.ListKey == key && x.IsActive).OrderByDescending(x => x.IsDefault).ThenBy(x => x.SortOrder).Select(x => x.Label).ToListAsync(ct);

    public async Task<bool> IsActiveOptionAsync(string key, string? label, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(label) || await db.SequflohReferenceValues.AnyAsync(x => x.ListKey == key && x.Label == label && x.IsActive, ct);

    public async Task RequireOptionAsync(string key, string? label, string what, CancellationToken ct)
    {
        if (!await IsActiveOptionAsync(key, label, ct)) throw new AppException($"{what} '{label}' is not an active value of the {Def(key).Label.ToLowerInvariant()} list.");
    }

    static string CodeOf(string label) => new string(label.Trim().ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_') is { Length: > 0 } c ? (c.Length > 60 ? c[..60] : c) : "VALUE";

    public async Task<SequflohReferenceValue> CreateValueAsync(string key, ReferenceValueInput r, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        Def(key);
        ModuleSupport.Required(r.Label, "Label", 200);
        var code = string.IsNullOrWhiteSpace(r.Code) ? CodeOf(r.Label) : r.Code.Trim().ToUpperInvariant();
        if (code.Length > 60) throw new AppException("The code is limited to 60 characters.");
        if (await db.SequflohReferenceValues.AnyAsync(x => x.ListKey == key && (x.Code == code || x.Label == r.Label.Trim()), ct)) throw new ConflictException("A value with this code or label already exists in the list.");
        var order = (await db.SequflohReferenceValues.Where(x => x.ListKey == key).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 10;
        var v = new SequflohReferenceValue { ListKey = key, Code = code, Label = r.Label.Trim(), Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(), SortOrder = order, IsActive = r.IsActive };
        db.SequflohReferenceValues.Add(v);
        if (r.IsDefault) await SetDefaultInternalAsync(v, ct);
        await db.SaveChangesAsync(ct);
        return v;
    }

    public async Task UpdateValueAsync(Guid id, ReferenceValueInput r, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        var v = await db.SequflohReferenceValues.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Reference value");
        if (r.RowVersion != 0) db.Entry(v).Property(x => x.RowVersion).OriginalValue = r.RowVersion;
        ModuleSupport.Required(r.Label, "Label", 200);
        if (await db.SequflohReferenceValues.AnyAsync(x => x.ListKey == v.ListKey && x.Id != id && x.Label == r.Label.Trim(), ct)) throw new ConflictException("Another value of the list has this label.");
        var renamed = v.Label != r.Label.Trim();
        v.Label = r.Label.Trim(); v.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (!r.IsActive && v.IsDefault) throw new AppException("Choose another default before deactivating the default value.");
        v.IsActive = r.IsActive;
        if (r.IsDefault && !v.IsDefault) await SetDefaultInternalAsync(v, ct);
        else if (!r.IsDefault) v.IsDefault = false;
        await db.SaveChangesAsync(ct);
        if (renamed && v.ListKey == SequflohCatalog.ProductFamilies)
            await db.Parts.Where(p => p.TypeId == v.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ProductFamily, v.Label), ct);
    }

    /// <summary>
    /// Each product family with its confirmed validation form, and a suggested blank form whose title contains the family name and "validation".
    /// The suggestion is only shown; an administrator confirms it by saving.
    /// </summary>
    public async Task<object> ValidationFormsAsync(CancellationToken ct)
    {
        var families = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.IsActive).OrderBy(v => v.SortOrder).ThenBy(v => v.Label)
            .Select(v => new { v.Id, v.Label, v.Code, v.ValidationFormId }).ToListAsync(ct);
        var blanks = await db.ImportedFiles.AsNoTracking().Where(b => b.IsBlankForm && !b.IsArchived).OrderBy(b => b.Title).Select(b => new { b.Id, b.Number, b.Title }).ToListAsync(ct);
        static bool Has(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);
        var salt = (await db.SequflohSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.SaltSprayFormId;
        return new
        {
            Blanks = blanks,
            // tests are not product families; each row works the same way: a suggestion is never used until saved
            Tests = new[] { new { Key = "salt-spray", Label = "Salt Spray Test", BlankFormId = salt,
                SuggestedId = blanks.Where(b => Has(b.Title, "salt spray")).Select(b => (Guid?)b.Id).FirstOrDefault() } },
            Families = families.Select(v => new
            {
                v.Id, v.Label, BlankFormId = v.ValidationFormId,
                SuggestedId = blanks.Where(b => Has(b.Title, "validation") && (Has(b.Title, v.Label) || (v.Code.Length > 1 && Has(b.Title, v.Code)))).Select(b => (Guid?)b.Id).FirstOrDefault()
            })
        };
    }

    public async Task SetValidationFormAsync(Guid familyId, Guid? blankId, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageSettings, ct);
        var v = await db.SequflohReferenceValues.FirstOrDefaultAsync(x => x.Id == familyId && x.ListKey == SequflohCatalog.ProductFamilies, ct) ?? throw new NotFoundException("Product family");
        string? title = null;
        if (blankId is { } b)
            title = await db.ImportedFiles.Where(x => x.Id == b && x.IsBlankForm && !x.IsArchived).Select(x => x.Number + " " + x.Title).FirstOrDefaultAsync(ct) ?? throw new AppException("Choose a blank form that is not archived.");
        if (v.ValidationFormId == blankId) return;
        var before = v.ValidationFormId is { } old ? await db.ImportedFiles.Where(x => x.Id == old).Select(x => x.Number + " " + x.Title).FirstOrDefaultAsync(ct) : null;
        v.ValidationFormId = blankId;
        Audit("ValidationFormConfirmed", nameof(SequflohReferenceValue), v.Id, $"Validation form for {v.Label} changed from {before ?? "none"} to {title ?? "none"}");
        await db.SaveChangesAsync(ct);
    }

    public async Task SetTestFormAsync(string key, Guid? blankId, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageSettings, ct);
        if (key != "salt-spray") throw new NotFoundException("Test");
        var s = await RowAsync(ct);
        string? title = null;
        if (blankId is { } b)
            title = await db.ImportedFiles.Where(x => x.Id == b && x.IsBlankForm && !x.IsArchived).Select(x => x.Number + " " + x.Title).FirstOrDefaultAsync(ct) ?? throw new AppException("Choose a blank form that is not archived.");
        if (s.SaltSprayFormId == blankId) return;
        var before = s.SaltSprayFormId is { } old ? await db.ImportedFiles.Where(x => x.Id == old).Select(x => x.Number + " " + x.Title).FirstOrDefaultAsync(ct) : null;
        s.SaltSprayFormId = blankId;
        Audit("ValidationFormConfirmed", nameof(SequflohSettings), s.Id, $"Form for Salt Spray Test changed from {before ?? "none"} to {title ?? "none"}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The fields that start as Not applicable on new and imported parts of a product family.</summary>
    public async Task SetFamilyHiddenFieldsAsync(Guid id, List<string>? keys, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        var v = await db.SequflohReferenceValues.FirstOrDefaultAsync(x => x.Id == id && x.ListKey == SequflohCatalog.ProductFamilies, ct) ?? throw new NotFoundException("Product family");
        v.HiddenFields = PartFields.Write(keys);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid id, bool active, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        var v = await db.SequflohReferenceValues.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Reference value");
        if (!active && v.IsDefault) throw new AppException("Choose another default before deactivating the default value.");
        v.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        var v = await db.SequflohReferenceValues.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Reference value");
        if (!v.IsActive) throw new AppException("Only an active value can be the default.");
        await SetDefaultInternalAsync(v, ct);
        await db.SaveChangesAsync(ct);
    }

    async Task SetDefaultInternalAsync(SequflohReferenceValue v, CancellationToken ct)
    {
        foreach (var o in await db.SequflohReferenceValues.Where(x => x.ListKey == v.ListKey && x.IsDefault && x.Id != v.Id).ToListAsync(ct)) o.IsDefault = false;
        v.IsDefault = true;
    }

    public async Task ReorderAsync(string key, ReorderInput r, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        var values = await db.SequflohReferenceValues.Where(x => x.ListKey == key).ToListAsync(ct);
        if (r.Ids.Count != values.Count || r.Ids.Distinct().Count() != values.Count || r.Ids.Any(id => values.All(v => v.Id != id)))
            throw new AppException("The new order must list every value of the list exactly once.");
        for (var i = 0; i < r.Ids.Count; i++) values.First(v => v.Id == r.Ids[i]).SortOrder = (i + 1) * 10;
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> ValueHistoryAsync(Guid id, CancellationToken ct) =>
        await db.AuditEntries.AsNoTracking().Where(a => a.EntityType == nameof(SequflohReferenceValue) && a.EntityId == id).OrderByDescending(a => a.OccurredAt)
            .Select(a => new { a.OccurredAt, a.UserName, a.Action, a.Description, a.Changes }).Take(200).ToListAsync(ct);

    /// <summary>Explicit, confirmed reset: restores missing seeded values and the seeded labels and order of one list. Administrator-added values stay.</summary>
    public async Task<int> ResetListAsync(string key, bool confirmed, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageDropdowns, ct);
        if (!confirmed) throw new AppException("Confirm the reset: seeded labels and order of this list will be restored.");
        var def = Def(key);
        var values = await db.SequflohReferenceValues.Where(x => x.ListKey == key).ToListAsync(ct);
        var changed = 0;
        for (var i = 0; i < def.Seed.Length; i++)
        {
            var code = CodeOf(def.Seed[i]);
            var v = values.FirstOrDefault(x => x.Code == code);
            if (v is null) { db.SequflohReferenceValues.Add(new SequflohReferenceValue { ListKey = key, Code = code, Label = def.Seed[i], SortOrder = (i + 1) * 10, IsSeeded = true, IsDefault = i == 0 && !def.NoDefault && values.All(x => !x.IsDefault) }); changed++; continue; }
            if (v.Label != def.Seed[i] || v.SortOrder != (i + 1) * 10 || !v.IsActive) { v.Label = def.Seed[i]; v.SortOrder = (i + 1) * 10; v.IsActive = true; changed++; }
        }
        Audit("SeedReset", "SequflohReferenceList", Guid.Empty, $"Reset SEQUFLOH reference list {def.Label} to seeded values ({changed} changed)", context: new { list = key });
        await db.SaveChangesAsync(ct);
        return changed;
    }

    // ------------------------------------------------------------------ module access (existing roles)
    public async Task<object> ModuleAccessAsync(CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageModuleAccess, ct);
        var roles = await db.QmsRoles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
        var settings = await GetSettingsAsync(ct);
        return new
        {
            Permissions = SequflohCatalog.AllPermissions,
            Roles = roles.Select(r => new { r.Id, r.Name, r.Description, r.IsSystem, r.RowVersion, Permissions = r.Permissions.Select(p => p.Permission).Where(p => p.StartsWith("sequfloh.", StringComparison.Ordinal)).Order().ToList(),
                Users = db.UserRoleAssignments.Count(a => a.RoleId == r.Id) }),
            Suggestions = settings.Security.RoleSuggestions
        };
    }

    /// <summary>Replaces only the SEQUFLOH permissions of an existing role; other permissions are untouched. The built-in administrator role always holds all.</summary>
    public async Task SetRoleAccessAsync(Guid roleId, RoleAccessInput r, CancellationToken ct)
    {
        await RequireOrgAsync(Permissions.SequflohManageModuleAccess, ct);
        if (r.Permissions.Any(p => !SequflohCatalog.AllPermissions.Contains(p))) throw new AppException("Only SEQUFLOH permissions can be changed here.");
        var role = await db.QmsRoles.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == roleId, ct) ?? throw new NotFoundException("Role");
        if (role.IsSystem) throw new AppException("The built-in administrator role always holds every permission.");
        if (r.RowVersion is { } rv) db.Entry(role).Property(x => x.RowVersion).OriginalValue = rv;
        var before = role.Permissions.Where(p => p.Permission.StartsWith("sequfloh.", StringComparison.Ordinal)).Select(p => p.Permission).Order().ToList();
        var after = r.Permissions.Distinct().Order().ToList();
        if (after.Count > 0 && !after.Contains(Permissions.SequflohAccess)) throw new AppException("Include sequfloh.access: every other SEQUFLOH permission needs module access.");
        foreach (var p in role.Permissions.Where(p => p.Permission.StartsWith("sequfloh.", StringComparison.Ordinal) && !after.Contains(p.Permission)).ToList()) role.Permissions.Remove(p);
        foreach (var p in after.Where(p => before.All(b => b != p))) role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
        role.UpdatedAt = m.Now;
        Audit("ModuleAccessChanged", "Role", role.Id, $"Changed SEQUFLOH permissions of role {role.Name}", reason: null,
            context: new { added = after.Except(before).ToList(), removed = before.Except(after).ToList() });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ recent and pinned items
    public async Task<object> UserItemsAsync(CancellationToken ct)
    {
        var uid = m.UserId;
        var limit = (await GetSettingsAsync(ct)).General.RecentItemsLimit;
        var items = await db.SequflohUserItems.AsNoTracking().Where(x => x.UserId == uid).OrderByDescending(x => x.LastOpenedAt).ToListAsync(ct);
        return new
        {
            Recent = items.Where(x => x.Kind == "recent").Take(limit).Select(x => new { x.Id, x.ItemType, x.RecordId, x.Path, x.Label, x.LastOpenedAt }),
            Favorites = items.Where(x => x.Kind == "favorite").Select(x => new { x.Id, x.ItemType, x.RecordId, x.Path, x.Label, x.LastOpenedAt })
        };
    }

    public async Task TouchAsync(UserItemInput r, CancellationToken ct)
    {
        var uid = m.UserId;
        if (r.Kind is not ("recent" or "favorite")) throw new AppException("Kind must be recent or favorite.");
        if (!r.Path.StartsWith("/sequfloh", StringComparison.Ordinal) || r.Path.Length > 400) throw new AppException("Only SEQUFLOH pages can be kept here.");
        ModuleSupport.Required(r.Label, "Label", 300);
        if (r.Kind == "favorite" && !(await GetSettingsAsync(ct)).General.FavoritesEnabled) throw new AppException("Favorites are switched off in the SEQUFLOH settings.");
        var x = await db.SequflohUserItems.FirstOrDefaultAsync(i => i.UserId == uid && i.Kind == r.Kind && i.Path == r.Path, ct);
        if (x is null) { x = new SequflohUserItem { UserId = uid, Kind = r.Kind, Path = r.Path }; db.SequflohUserItems.Add(x); }
        x.ItemType = r.ItemType.Length > 60 ? r.ItemType[..60] : r.ItemType; x.RecordId = r.RecordId; x.Label = r.Label.Trim(); x.LastOpenedAt = m.Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* the same item was recorded concurrently by another tab */ }
        if (r.Kind == "recent")
        {
            var old = await db.SequflohUserItems.Where(i => i.UserId == uid && i.Kind == "recent").OrderByDescending(i => i.LastOpenedAt).Skip(50).ToListAsync(ct);
            if (old.Count > 0) { db.SequflohUserItems.RemoveRange(old); await db.SaveChangesAsync(ct); }
        }
    }

    public async Task RemoveFavoriteAsync(Guid id, CancellationToken ct)
    {
        var uid = m.UserId;
        var x = await db.SequflohUserItems.FirstOrDefaultAsync(i => i.Id == id && i.UserId == uid && i.Kind == "favorite", ct) ?? throw new NotFoundException("Favorite");
        db.SequflohUserItems.Remove(x);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ current user context
    public async Task<object> ContextAsync(CancellationToken ct)
    {
        var uid = m.UserId;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => new { u.Id, u.DisplayName, u.Email, u.DepartmentId }).FirstAsync(ct);
        var roles = await db.UserRoleAssignments.AsNoTracking().Where(a => a.UserId == uid).Join(db.QmsRoles, a => a.RoleId, r => r.Id, (a, r) => new { r.Name, a.SiteId })
            .ToListAsync(ct);
        var siteIds = roles.Where(r => r.SiteId != null).Select(r => r.SiteId!.Value).Distinct().ToList();
        var sites = await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).Select(s => new { s.Id, s.Code, s.Name }).ToListAsync(ct);
        var granted = new List<string>();
        foreach (var p in SequflohCatalog.AllPermissions) if (await access.HasPermissionAsync(uid, p, ct)) granted.Add(p);
        var org = await db.OrganizationSettings.AsNoTracking().Select(o => new { o.OrganizationName, o.ApplicationName }).FirstOrDefaultAsync(ct);
        var settings = await GetSettingsAsync(ct);
        return new
        {
            User = user, Roles = roles.Select(r => r.Name).Distinct().Order(), Sites = sites, Permissions = granted, Organization = org?.OrganizationName, Application = org?.ApplicationName,
            Module = new { Key = SequflohCatalog.Module, settings.General.DisplayName, Subtitle = "Enterprise Execution Engine", settings.General.Description, settings.General.ModuleEnabled, settings.General.RecordsPerPage, settings.General.FavoritesEnabled, settings.General.DefaultLandingPage }
        };
    }

    async Task RequireOrgAsync(string permission, CancellationToken ct)
    {
        if (!await access.HasPermissionAsync(m.UserId, permission, ct)) throw new ForbiddenException();
    }

    // ------------------------------------------------------------------ seeding
    /// <summary>
    /// Idempotent SEQUFLOH seed: settings row, reference values and suggested permission sets. Only missing items are added;
    /// administrator-modified values are never overwritten (see <see cref="ResetListAsync"/> for the confirmed reset). No users or roles are created.
    /// </summary>
    public async Task<int> EnsureSeededAsync(CancellationToken ct) => await SeedAsync(db, ct);

    public static async Task<int> SeedAsync(AppDbContext db, CancellationToken ct)
    {
        var added = 0;
        if (!await db.SequflohSettings.AnyAsync(ct))
        {
            db.SequflohSettings.Add(new SequflohSettings
            {
                General = SequflohJson.Write(new GeneralSettings()), Numbering = SequflohJson.Write(new NumberingSettings()), WorkInstructions = SequflohJson.Write(new WorkInstructionSettings()),
                Tracking = SequflohJson.Write(new TrackingSettings()), Validation = SequflohJson.Write(new ValidationSettings()), Fai = SequflohJson.Write(new FaiSettings()),
                Repair = SequflohJson.Write(new RepairSettings()), Display = SequflohJson.Write(new DisplaySettings()), Export = SequflohJson.Write(new ExportSettings()),
                Notifications = SequflohJson.Write(new NotificationSettings()), Security = SequflohJson.Write(new SecuritySettings { RoleSuggestions = SequflohCatalog.DefaultRoleSuggestions() })
            });
            added++;
        }
        var existing = await db.SequflohReferenceValues.Select(v => new { v.ListKey, v.Code }).ToListAsync(ct);
        foreach (var l in SequflohCatalog.Lists)
            for (var i = 0; i < l.Seed.Length; i++)
            {
                var code = CodeOf(l.Seed[i]);
                if (existing.Any(e => e.ListKey == l.Key && e.Code == code)) continue;
                db.SequflohReferenceValues.Add(new SequflohReferenceValue { ListKey = l.Key, Code = code, Label = l.Seed[i], SortOrder = (i + 1) * 10, IsSeeded = true, IsDefault = i == 0 && !l.NoDefault && existing.All(e => e.ListKey != l.Key) });
                added++;
            }
        if (added > 0) await db.SaveChangesAsync(ct);
        await LinkProductFamiliesAsync(db, ct); // not counted: it links parts, it does not seed
        return added;
    }

    /// <summary>
    /// Parts that only have a product family typed as text are linked to the matching value of the product families list
    /// (label, code, or either part of a label such as "Complete Strut Assembly (CSA)"); a text with no match is added to the list.
    /// </summary>
    public static async Task<int> LinkProductFamiliesAsync(AppDbContext db, CancellationToken ct)
    {
        var texts = await db.Parts.Where(p => p.TypeId == null && p.ProductFamily != null && p.ProductFamily != "").Select(p => p.ProductFamily!).Distinct().ToListAsync(ct);
        if (texts.Count == 0) return 0;
        var values = await db.SequflohReferenceValues.Where(v => v.ListKey == SequflohCatalog.ProductFamilies).ToListAsync(ct);
        var sort = values.Count == 0 ? 0 : values.Max(v => v.SortOrder);
        foreach (var text in texts)
        {
            var v = values.FirstOrDefault(x => ProductFamilyMatches(x.Code, x.Label, text.Trim()));
            if (v is null)
            {
                var code = CodeOf(text);
                for (var n = 2; values.Any(x => x.Code == code); n++) code = $"{CodeOf(text)}_{n}";
                v = new SequflohReferenceValue { ListKey = SequflohCatalog.ProductFamilies, Code = code, Label = text.Trim(), SortOrder = sort += 10 };
                db.SequflohReferenceValues.Add(v);
                values.Add(v);
                await db.SaveChangesAsync(ct);
            }
            var id = v.Id; var label = v.Label;
            await db.Parts.Where(p => p.TypeId == null && p.ProductFamily == text).ExecuteUpdateAsync(s => s.SetProperty(p => p.TypeId, id).SetProperty(p => p.ProductFamily, label), ct);
        }
        return texts.Count;
    }

    /// <summary>True when the text names the value: its label or code, or the text before or inside the brackets of a label like "Complete Strut Assembly (CSA)".</summary>
    public static bool ProductFamilyMatches(string code, string label, string text)
    {
        static string N(string s) => string.Join(' ', s.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        var t = N(text);
        if (t.Length == 0) return false;
        if (t == N(label) || t.Replace(' ', '_') == code.ToUpperInvariant()) return true;
        var m = System.Text.RegularExpressions.Regex.Match(label, @"^(.*?)\s*\(([^)]*)\)\s*$");
        return m.Success && (t == N(m.Groups[1].Value) || t == N(m.Groups[2].Value));
    }
}
