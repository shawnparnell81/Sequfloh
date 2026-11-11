using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record CustomScreen(string Key, string Group, string Label, string RecordType);

public sealed record CustomPlacementInput(string Screen, bool Required, bool ShowInList, bool IncludeInCsv, bool Filterable);

public sealed record CustomValueInput(Guid? Id, string Label, bool IsActive, bool IsDefault);

public sealed record CustomDropdownInput(string Name, string? Description, List<CustomValueInput> Values, List<CustomPlacementInput> Placements);

public sealed record CustomValueChoice(Guid Id, string Label, bool IsActive, bool IsDefault);

public sealed record CustomFieldView(Guid Id, string Name, bool Required, bool ShowInList, bool IncludeInCsv, bool Filterable, Guid? ValueId, string? ValueLabel, List<CustomValueChoice> Values);

public sealed record CustomValueSet(Guid DropdownId, Guid? ValueId);

/// <summary>Administrator-defined dropdowns and the value chosen on each record. Built-in reference lists are not stored here.</summary>
public sealed class CustomDropdownService(AppDbContext db, SequflohFoundationService f)
{
    public static readonly CustomScreen[] Screens =
    [
        new("parts", "Parts", "Parts", "Part"),
        new("drawings", "Drawings", "Drawings", "Drawing"),
        new("work-orders:Validation", "Work orders", "Validation", "Tracking"),
        new("work-orders:Fai", "Work orders", "FAI", "Tracking"),
        new("work-orders:Repair", "Work orders", "Repair / Rework", "Tracking"),
        new("work-orders:Production", "Work orders", "Production", "Tracking"),
        new("work-orders:Inspection", "Work orders", "Inspection", "Tracking"),
        new("work-orders:Warranty", "Work orders", "Warranty", "Tracking"),
        new("work-orders:SaltSpray", "Work orders", "Salt Spray Test", "Tracking"),
        new("work-orders:Development", "Work orders", "Development", "Tracking"),
        new("work-orders:Rebox", "Work orders", "Rebox", "Tracking"),
        new("work-orders:Test", "Work orders", "Test", "Tracking"),
        new("work-orders:Weight", "Work orders", "Weight", "Tracking"),
        new("work-orders:Research", "Work orders", "Research", "Tracking"),
        new("inspection-requests", "Inspection requests", "Inspection requests", "InspectionRequest"),
        new("work-instructions", "Work instructions", "Work instructions and procedures", "WorkInstruction"),
        new("ncr", "NCR", "NCR / findings", "Tracking"),
        new("fai-validation", "FAI / Validation", "FAI and validation records", "Tracking"),
        new("projects", "Projects", "Projects", "Project"),
        new("chargebacks", "Chargebacks", "Chargeback reports", "Chargeback"),
    ];

    static CustomScreen Screen(string key) => Screens.FirstOrDefault(s => s.Key == key) ?? throw new AppException("Choose a screen from the list.");

    public IReadOnlyList<CustomScreen> ScreenList() => Screens;

    public async Task<List<object>> ListAsync(CancellationToken ct)
    {
        var rows = await db.CustomDropdowns.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        var ids = rows.Select(d => d.Id).ToList();
        var counts = await db.CustomFieldValues.AsNoTracking().Where(v => ids.Contains(v.DropdownId) && v.ValueId != null).GroupBy(v => v.DropdownId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var places = await db.CustomDropdownPlacements.AsNoTracking().Where(p => ids.Contains(p.DropdownId)).ToListAsync(ct);
        return rows.Select(d => (object)new
        {
            d.Id, d.Name, d.Description,
            Values = db.CustomDropdownValues.Count(v => v.DropdownId == d.Id),
            UsedBy = counts.FirstOrDefault(c => c.Key == d.Id)?.N ?? 0,
            Screens = places.Where(p => p.DropdownId == d.Id).Select(p => p.Screen).ToList(),
        }).ToList();
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var d = await db.CustomDropdowns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Dropdown");
        var values = await db.CustomDropdownValues.AsNoTracking().Where(v => v.DropdownId == id).OrderBy(v => v.SortOrder).ThenBy(v => v.Label)
            .Select(v => new { v.Id, v.Label, v.Code, v.SortOrder, v.IsActive, v.IsDefault }).ToListAsync(ct);
        var placements = await db.CustomDropdownPlacements.AsNoTracking().Where(p => p.DropdownId == id)
            .Select(p => new { p.Screen, p.Required, p.ShowInList, p.IncludeInCsv, p.Filterable }).ToListAsync(ct);
        var used = await db.CustomFieldValues.CountAsync(v => v.DropdownId == id && v.ValueId != null, ct);
        return new { d.Id, d.Name, d.Description, d.RowVersion, used, values, placements };
    }

    public async Task<Guid> CreateAsync(CustomDropdownInput r, CancellationToken ct)
    {
        var name = CleanName(r.Name);
        if (await db.CustomDropdowns.AnyAsync(d => d.Name.ToLower() == name.ToLower(), ct)) throw new AppException($"A dropdown named {name} already exists.");
        var d = new CustomDropdown { Name = name, Description = CleanDescription(r.Description) };
        db.CustomDropdowns.Add(d);
        WriteValues(d.Id, r.Values);
        WritePlacements(d.Id, r.Placements);
        f.Audit("Created", nameof(CustomDropdown), d.Id, $"Created dropdown {d.Name}." + PlaceText(r.Placements));
        await db.SaveChangesAsync(ct);
        return d.Id;
    }

    public async Task UpdateAsync(Guid id, CustomDropdownInput r, CancellationToken ct)
    {
        var d = await db.CustomDropdowns.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Dropdown");
        var name = CleanName(r.Name);
        if (await db.CustomDropdowns.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower(), ct)) throw new AppException($"A dropdown named {name} already exists.");
        var before = d.Name;
        d.Name = name;
        d.Description = CleanDescription(r.Description);
        await SyncValuesAsync(d, r.Values, ct);
        var oldPlaces = await db.CustomDropdownPlacements.Where(p => p.DropdownId == id).ToListAsync(ct);
        db.CustomDropdownPlacements.RemoveRange(oldPlaces);
        WritePlacements(id, r.Placements);
        f.Audit("Updated", nameof(CustomDropdown), d.Id, before == d.Name ? $"Updated dropdown {d.Name}." + PlaceText(r.Placements) : $"Renamed dropdown {before} to {d.Name}." + PlaceText(r.Placements));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, string? confirm, CancellationToken ct)
    {
        var d = await db.CustomDropdowns.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Dropdown");
        var used = await db.CustomFieldValues.CountAsync(v => v.DropdownId == id && v.ValueId != null, ct);
        if (used > 0 && !string.Equals(confirm?.Trim(), d.Name, StringComparison.Ordinal))
            throw new AppException($"Type {d.Name} to delete this dropdown. It is used on {used} {(used == 1 ? "record" : "records")}.");
        var saved = await db.CustomFieldValues.Where(v => v.DropdownId == id).ToListAsync(ct);
        db.CustomFieldValues.RemoveRange(saved);
        if (saved.Count > 0) await db.SaveChangesAsync(ct);
        db.CustomDropdownValues.RemoveRange(await db.CustomDropdownValues.Where(v => v.DropdownId == id).ToListAsync(ct));
        db.CustomDropdownPlacements.RemoveRange(await db.CustomDropdownPlacements.Where(p => p.DropdownId == id).ToListAsync(ct));
        db.CustomDropdowns.Remove(d);
        f.Audit("Deleted", nameof(CustomDropdown), d.Id, used == 0 ? $"Deleted dropdown {d.Name}." : $"Deleted dropdown {d.Name}. It was cleared from {used} {(used == 1 ? "record" : "records")}.");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fields a screen shows. recordId fills in the saved choice when the record already has one.</summary>
    public async Task<List<CustomFieldView>> ForScreenAsync(string screen, string? recordType, Guid? recordId, CancellationToken ct)
    {
        Screen(screen);
        var places = await db.CustomDropdownPlacements.AsNoTracking().Where(p => p.Screen == screen).ToListAsync(ct);
        if (places.Count == 0) return [];
        var ids = places.Select(p => p.DropdownId).Distinct().ToList();
        var dropdowns = await db.CustomDropdowns.AsNoTracking().Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var values = await db.CustomDropdownValues.AsNoTracking().Where(v => ids.Contains(v.DropdownId)).OrderBy(v => v.SortOrder).ThenBy(v => v.Label).ToListAsync(ct);
        var chosen = recordId is { } rid && !string.IsNullOrWhiteSpace(recordType)
            ? await db.CustomFieldValues.AsNoTracking().Where(v => v.RecordType == recordType && v.RecordId == rid && ids.Contains(v.DropdownId)).ToListAsync(ct)
            : [];
        return places.Select(p =>
        {
            var d = dropdowns.FirstOrDefault(x => x.Id == p.DropdownId);
            if (d is null) return null;
            var mine = values.Where(v => v.DropdownId == d.Id).ToList();
            var pick = chosen.FirstOrDefault(c => c.DropdownId == d.Id);
            var label = pick?.ValueId is { } vid ? mine.FirstOrDefault(v => v.Id == vid)?.Label : null;
            return new CustomFieldView(d.Id, d.Name, p.Required, p.ShowInList, p.IncludeInCsv, p.Filterable, pick?.ValueId, label,
                mine.Where(v => v.IsActive || v.Id == pick?.ValueId).Select(v => new CustomValueChoice(v.Id, v.Label, v.IsActive, v.IsDefault)).ToList());
        }).Where(x => x is not null).Cast<CustomFieldView>().OrderBy(x => x.Name).ToList();
    }

    public async Task SaveValuesAsync(string recordType, Guid recordId, IReadOnlyList<CustomValueSet> values, string? recordLabel, CancellationToken ct)
    {
        var screens = Screens.Where(s => s.RecordType == recordType).Select(s => s.Key).ToList();
        var places = await db.CustomDropdownPlacements.AsNoTracking().Where(p => screens.Contains(p.Screen)).ToListAsync(ct);
        var allowed = places.Select(p => p.DropdownId).Distinct().ToList();
        foreach (var set in values)
        {
            if (!allowed.Contains(set.DropdownId)) throw new AppException("That dropdown is not shown on this record.");
            if (set.ValueId is { } vid && !await db.CustomDropdownValues.AnyAsync(v => v.Id == vid && v.DropdownId == set.DropdownId && v.IsActive, ct))
                throw new AppException("Choose an active value.");
        }
        var required = places.Where(p => p.Required).Select(p => p.DropdownId).Distinct();
        foreach (var id in required)
            if (values.All(v => v.DropdownId != id || v.ValueId is null))
            {
                var name = await db.CustomDropdowns.Where(d => d.Id == id).Select(d => d.Name).FirstAsync(ct);
                throw new AppException($"{name} is required.");
            }
        var existing = await db.CustomFieldValues.Where(v => v.RecordType == recordType && v.RecordId == recordId).ToListAsync(ct);
        foreach (var set in values.GroupBy(v => v.DropdownId).Select(g => g.Last()))
        {
            var row = existing.FirstOrDefault(v => v.DropdownId == set.DropdownId);
            var beforeId = row?.ValueId;
            if (beforeId == set.ValueId) continue;
            if (row is null)
            {
                if (set.ValueId is null) continue;
                row = new CustomFieldValue { DropdownId = set.DropdownId, RecordType = recordType, RecordId = recordId, ValueId = set.ValueId };
                db.CustomFieldValues.Add(row);
            }
            else row.ValueId = set.ValueId;
            var drop = await db.CustomDropdowns.Where(d => d.Id == set.DropdownId).Select(d => d.Name).FirstAsync(ct);
            var before = beforeId is { } b ? await db.CustomDropdownValues.Where(v => v.Id == b).Select(v => v.Label).FirstOrDefaultAsync(ct) : null;
            var after = set.ValueId is { } a ? await db.CustomDropdownValues.Where(v => v.Id == a).Select(v => v.Label).FirstOrDefaultAsync(ct) : null;
            var who = string.IsNullOrWhiteSpace(recordLabel) ? recordType : recordLabel;
            var text = after is null ? $"Cleared {drop} on {who}" + (before is null ? "." : $" (was {before}).")
                : before is null ? $"Set {drop} on {who} to {after}." : $"Changed {drop} on {who} from {before} to {after}.";
            f.Audit(after is null ? "Cleared" : "Updated", recordType, recordId, text);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Columns a parts list should add, and the choice to filter by.</summary>
    public async Task<List<object>> PartColumnsAsync(CancellationToken ct)
    {
        var places = await db.CustomDropdownPlacements.AsNoTracking().Where(p => p.Screen == "parts" && (p.ShowInList || p.Filterable || p.IncludeInCsv)).ToListAsync(ct);
        var ids = places.Select(p => p.DropdownId).Distinct().ToList();
        var names = await db.CustomDropdowns.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var values = await db.CustomDropdownValues.AsNoTracking().Where(v => ids.Contains(v.DropdownId) && v.IsActive).OrderBy(v => v.SortOrder).ThenBy(v => v.Label)
            .Select(v => new { v.Id, v.DropdownId, v.Label }).ToListAsync(ct);
        return places.Where(p => names.ContainsKey(p.DropdownId)).Select(p => (object)new
        {
            p.DropdownId, Name = names[p.DropdownId], p.ShowInList, p.Filterable, p.IncludeInCsv,
            Values = values.Where(v => v.DropdownId == p.DropdownId).Select(v => new { v.Id, v.Label }).ToList(),
        }).ToList();
    }

    public async Task<Dictionary<Guid, List<(Guid DropdownId, string Name, string Label)>>> ForPartsAsync(List<Guid> partIds, CancellationToken ct)
    {
        var map = partIds.Distinct().ToDictionary(id => id, _ => new List<(Guid, string, string)>());
        if (map.Count == 0) return map;
        var shown = await (
            from p in db.CustomDropdownPlacements.AsNoTracking()
            join d in db.CustomDropdowns.AsNoTracking() on p.DropdownId equals d.Id
            where p.Screen == "parts" && p.ShowInList
            select new { d.Id, d.Name }).ToListAsync(ct);
        var ids = shown.Select(s => s.Id).ToList();
        if (ids.Count == 0) return map;
        var keys = map.Keys.ToList();
        var rows = await (
            from v in db.CustomFieldValues.AsNoTracking()
            join val in db.CustomDropdownValues.AsNoTracking() on v.ValueId equals val.Id
            where v.RecordType == "Part" && keys.Contains(v.RecordId) && ids.Contains(v.DropdownId)
            select new { v.RecordId, v.DropdownId, val.Label }).ToListAsync(ct);
        foreach (var row in rows)
            if (shown.FirstOrDefault(s => s.Id == row.DropdownId) is { } drop)
                map[row.RecordId].Add((drop.Id, drop.Name, row.Label));
        return map;
    }

    public IQueryable<Guid> PartsWithValue(Guid dropdownId, Guid? valueId) =>
        db.CustomFieldValues.AsNoTracking().Where(v => v.RecordType == "Part" && v.DropdownId == dropdownId && v.ValueId == valueId).Select(v => v.RecordId);

    void WriteValues(Guid dropdownId, List<CustomValueInput>? values)
    {
        var list = values ?? [];
        if (list.Count(v => v.IsDefault && v.IsActive) > 1) throw new AppException("Choose one default value.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 10;
        foreach (var v in list)
        {
            var label = (v.Label ?? "").Trim();
            if (label.Length == 0) throw new AppException("Enter a value.");
            if (label.Length > 200) throw new AppException("A value is limited to 200 characters.");
            if (!seen.Add(label)) throw new AppException($"\"{label}\" is listed twice.");
            db.CustomDropdownValues.Add(new CustomDropdownValue
            {
                DropdownId = dropdownId, Label = label, Code = Code(label), SortOrder = order, IsActive = v.IsActive, IsDefault = v.IsDefault && v.IsActive,
            });
            order += 10;
        }
    }

    async Task SyncValuesAsync(CustomDropdown dropdown, List<CustomValueInput>? incoming, CancellationToken ct)
    {
        var list = incoming ?? [];
        if (list.Count(v => v.IsDefault && v.IsActive) > 1) throw new AppException("Choose one default value.");
        var existing = await db.CustomDropdownValues.Where(v => v.DropdownId == dropdown.Id).ToListAsync(ct);
        var keep = new HashSet<Guid>();
        var order = 10;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in list)
        {
            var label = (v.Label ?? "").Trim();
            if (label.Length == 0) throw new AppException("Enter a value.");
            if (!seen.Add(label)) throw new AppException($"\"{label}\" is listed twice.");
            var row = v.Id is { } id ? existing.FirstOrDefault(x => x.Id == id) : null;
            if (row is null)
                db.CustomDropdownValues.Add(new CustomDropdownValue { DropdownId = dropdown.Id, Label = label, Code = Code(label), SortOrder = order, IsActive = v.IsActive, IsDefault = v.IsDefault && v.IsActive });
            else
            {
                if (!string.Equals(row.Label, label, StringComparison.Ordinal))
                    f.Audit("Updated", nameof(CustomDropdown), dropdown.Id, $"Renamed a value of {dropdown.Name} from {row.Label} to {label}.");
                row.Label = label;
                row.SortOrder = order;
                row.IsActive = v.IsActive;
                row.IsDefault = v.IsDefault && v.IsActive;
                keep.Add(row.Id);
            }
            order += 10;
        }
        foreach (var gone in existing.Where(x => !keep.Contains(x.Id)))
        {
            if (await db.CustomFieldValues.AnyAsync(v => v.ValueId == gone.Id, ct))
                throw new AppException($"\"{gone.Label}\" is used on records. Deactivate it instead of deleting it.");
            db.CustomDropdownValues.Remove(gone);
            f.Audit("Deleted", nameof(CustomDropdown), dropdown.Id, $"Deleted unused value {gone.Label} from {dropdown.Name}.");
        }
    }

    void WritePlacements(Guid dropdownId, List<CustomPlacementInput>? placements)
    {
        foreach (var p in placements ?? [])
        {
            Screen(p.Screen);
            db.CustomDropdownPlacements.Add(new CustomDropdownPlacement
            {
                DropdownId = dropdownId, Screen = p.Screen, Required = p.Required, ShowInList = p.ShowInList, IncludeInCsv = p.IncludeInCsv, Filterable = p.Filterable,
            });
        }
    }

    static string PlaceText(List<CustomPlacementInput>? placements)
    {
        var names = (placements ?? []).Select(p => Screens.FirstOrDefault(s => s.Key == p.Screen)?.Label ?? p.Screen).ToList();
        return names.Count == 0 ? "" : " Shown on " + string.Join(", ", names) + ".";
    }

    static string CleanName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new AppException("Enter a name for the dropdown.");
        if (n.Length > 100) throw new AppException("The name is limited to 100 characters.");
        return n;
    }

    static string? CleanDescription(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (t.Length > 1000) throw new AppException("The description is limited to 1000 characters.");
        return t;
    }

    /// <summary>Applies mapped custom dropdown columns on a parts import. An unknown value is a row error and is not created.</summary>
    public async Task<PartsImportPlan> MergePartImportAsync(PartsImportRequest r, PartsImportPlan plan, bool save, CancellationToken ct)
    {
        var maps = r.Mapping.Where(kv => kv.Key.StartsWith("custom:", StringComparison.Ordinal) && Guid.TryParse(kv.Key.AsSpan(7), out _)).ToList();
        if (maps.Count == 0 || r.Tabs is not null) return plan;
        var ids = maps.Select(kv => Guid.Parse(kv.Key[7..])).Distinct().ToList();
        var places = await db.CustomDropdownPlacements.AsNoTracking().Where(p => p.Screen == "parts" && p.IncludeInCsv && ids.Contains(p.DropdownId)).ToListAsync(ct);
        var values = await db.CustomDropdownValues.AsNoTracking().Where(v => ids.Contains(v.DropdownId) && v.IsActive).ToListAsync(ct);
        var names = await db.CustomDropdowns.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var saved = save ? await db.Parts.AsNoTracking().Where(p => (plan.SavedPartIds ?? new List<Guid>()).Contains(p.Id)).Select(p => new { p.Id, p.PartNumber }).ToListAsync(ct) : [];
        var rows = plan.Rows.ToList();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var number = r.RowNumbers is { } nums && i < nums.Count ? nums[i] : i + 1;
            var at = rows.FindIndex(x => x.Row == number);
            if (at < 0) continue;
            var sets = new List<CustomValueSet>();
            var errors = new List<string>();
            foreach (var map in maps)
            {
                var drop = Guid.Parse(map.Key[7..]);
                var place = places.FirstOrDefault(p => p.DropdownId == drop);
                if (place is null || !names.TryGetValue(drop, out var name)) continue;
                var cell = map.Value >= 0 && map.Value < r.Rows[i].Count ? (r.Rows[i][map.Value] ?? "").Trim() : "";
                if (r.Fixed?.GetValueOrDefault(map.Key) is { Length: > 0 } fx) cell = fx.Trim();
                if (cell.Length == 0) { if (place.Required) errors.Add($"{name} is required."); continue; }
                var hit = values.FirstOrDefault(v => v.DropdownId == drop && string.Equals(v.Label.Trim(), cell, StringComparison.OrdinalIgnoreCase));
                if (hit is null) errors.Add($"{name} \"{cell}\" is not a value of that dropdown.");
                else sets.Add(new CustomValueSet(drop, hit.Id));
            }
            if (errors.Count > 0 && rows[at].Action is "Add" or "Update" or "Unchanged")
                rows[at] = rows[at] with { Action = "Error", Messages = errors };
            else if (save && errors.Count == 0 && sets.Count > 0 && rows[at].Action is "Add" or "Update")
            {
                var part = saved.FirstOrDefault(p => string.Equals(p.PartNumber, rows[at].PartNumber, StringComparison.OrdinalIgnoreCase));
                if (part is not null) await SaveValuesAsync("Part", part.Id, sets, $"part {part.PartNumber}", ct);
            }
        }
        return plan with
        {
            Rows = rows,
            Add = rows.Count(x => x.Action == "Add"),
            Update = rows.Count(x => x.Action == "Update"),
            Skip = rows.Count(x => x.Action == "Skip"),
            Errors = rows.Count(x => x.Action == "Error"),
            Unchanged = rows.Count(x => x.Action == "Unchanged"),
        };
    }

    static string Code(string label)
    {
        var c = new string(label.Trim().ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        return c.Length == 0 ? "VALUE" : c.Length > 60 ? c[..60] : c;
    }
}
