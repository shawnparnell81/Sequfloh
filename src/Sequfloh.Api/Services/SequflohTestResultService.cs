using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Tracking;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>One field a test template records: the drawing spec once per order and an actual value on every tested part.</summary>
public sealed record TestTemplateField(string Key, string Label, string DefaultUnit);
public sealed record TestTemplate(string Key, string Label, IReadOnlyList<TestTemplateField> Fields);

/// <summary>
/// The product family test templates. A product family record points at one of these by key (SequflohReferenceValue.TestTemplate); families
/// with no key get no test fields. The fields live here as data so an administrator screen can manage them later without code changes elsewhere.
/// </summary>
public static class SequflohTestTemplates
{
    public const string FuelPump = "fuel-pump";

    public static readonly IReadOnlyList<TestTemplate> All =
    [
        new(FuelPump, "Fuel pump test", [
            new("testPressure", "Test pressure", "psi"),
            new("shutoffPressure", "Shutoff pressure", "psi"),
            new("flowRate", "Flow rate", "L/h"),
            new("floatFull", "Float arm resistance - Full", "ohms"),
            new("floatEmpty", "Float arm resistance - Empty", "ohms"),
            new("amps", "Amps", "A"),
        ]),
    ];

    /// <summary>Links the fuel pump template to the family named exactly "Fuel Pump" (case and outer spaces ignored) that has no template yet. Nothing else on the family changes.</summary>
    public const string LinkFuelPumpSql = "UPDATE sqf.sequfloh_reference_values SET \"TestTemplate\" = 'fuel-pump' WHERE \"ListKey\" = 'product-families' AND \"TestTemplate\" IS NULL AND lower(btrim(\"Label\")) = 'fuel pump';";

    /// <summary>The key a Test order uses when its family has no template; its fields are only the ones added on the order.</summary>
    public const string OrderOnly = "order-only";
    static readonly TestTemplate Blank = new(OrderOnly, "Test", []);

    public static TestTemplate? Find(string? key) => key is null ? null : key == OrderOnly ? Blank : All.FirstOrDefault(t => t.Key == key);

    /// <summary>The template a seeded product family starts with (only Fuel Pump has one). Administrators' own families start with none.</summary>
    public static string? SeedFor(string listKey, string seedLabel) => listKey == SequflohCatalog.ProductFamilies && seedLabel == "Fuel Pump" ? FuelPump : null;
}

public sealed record TestValueInput(string Key, string? Value, string? Unit);
public sealed record TestSpecInput(List<TestValueInput>? Values, string? Reason);
public sealed record TestPartInput(List<TestValueInput>? Values, string? Note, string? Reason);
public sealed record TestRemoveInput(string? Reason);
public sealed record TestFieldInput(string? Label, string? Unit, string? Value, string? Reason);
public sealed record DrawingAttachInput(Guid DrawingId);

/// <summary>
/// Test work instruction results on Test work orders: the drawing spec entered once per order and Part 1, Part 2, ... actual results, each
/// shown against the spec. There is no automatic pass or fail. Filling a blank value is open to anyone working the order; changing a value
/// already entered needs the correct test results permission, and removing a part's results needs the remove permission. Both are audited, and
/// a removed part keeps its number so parts never renumber.
/// </summary>
public sealed class SequflohTestResultService(AppDbContext db, ModuleSupport m, ICurrentUser current, SequflohTrackingService tracking, SequflohFoundationService f)
{
    /// <summary>A stored value. Actuals and single-number specs use Value; a spec typed as a range keeps Text exactly as typed with its Min and Max.</summary>
    sealed record Val(string Key, decimal? Value, string? Unit, string? Text = null, decimal? Min = null, decimal? Max = null, bool? Strict = null);
    sealed record SpecVal(decimal? Value, string? Text, decimal? Min, decimal? Max, bool Strict = false);
    static readonly System.Text.RegularExpressions.Regex LimitRx = new($@"^\s*(≥|≤|>=|<=|＞=|＜=|≧|≦|>|<|＞|＜)\s*({Num})\s*$");
    const string Num = @"[-+]?\d+(?:\.\d+)?";
    static readonly System.Text.RegularExpressions.Regex RangeRx = new($@"^\s*({Num})\s*(?:-|–|to)\s*({Num})\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex TolRx = new($@"^\s*({Num})\s*(?:±|\+/-|\+-)\s*(\d+(?:\.\d+)?)\s*$");
    static decimal D(string x) => decimal.Parse(x, NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>A drawing spec as typed: one number (60), a range (320-360, 320 to 360) or a tolerance (45 ± 5, 45 +/- 5). Ranges keep the text exactly as typed.</summary>
    static SpecVal? SpecOf(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return new(d, null, null, null);
        if (t.Length <= 40)
        {
            var m = RangeRx.Match(t);
            if (m.Success) { var a = D(m.Groups[1].Value); var b = D(m.Groups[2].Value); return new(null, t, Math.Min(a, b), Math.Max(a, b)); }
            m = TolRx.Match(t);
            if (m.Success) { var c = D(m.Groups[1].Value); var e = D(m.Groups[2].Value); return new(null, t, c - e, c + e); }
            m = LimitRx.Match(t);
            if (m.Success)
            {
                var op = m.Groups[1].Value; var n = D(m.Groups[2].Value);
                var lower = op is "≥" or ">=" or ">" or "＞" or "≧" or "＞=";
                var strict = op is ">" or "<" or "＞" or "＜";
                return new(null, t, lower ? n : null, lower ? null : n, strict);
            }
        }
        throw new AppException($"{label} must be a number, a range such as 320-360, a tolerance such as 45 ± 5, or a limit such as ≥95 or ≤6.0.");
    }
    static string ST(Val? v) => v is null ? "" : v.Text ?? N(v.Value);
    static string SpecUnit(string text, string? u) => text == "" ? "blank" : $"{text}{(string.IsNullOrWhiteSpace(u) ? "" : " " + u)}";
    sealed record Custom(string Key, string Label, string DefaultUnit, string AddedBy, DateTimeOffset AddedAt, DateTimeOffset? RemovedAt = null, string? RemovedBy = null, string? RemoveReason = null);
    static List<Custom> ReadCustom(string? json) => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Custom>>(json, J) ?? [];
    static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    static List<Val> Read(string json) => JsonSerializer.Deserialize<List<Val>>(json, J) ?? [];
    static string Write(List<Val> v) => JsonSerializer.Serialize(v, J);

    public static string N(decimal? d) => d is null ? "" : d.Value.ToString("0.####", CultureInfo.InvariantCulture);
    static string WithUnit(decimal? d, string? u) => d is null ? "blank" : $"{N(d)}{(string.IsNullOrWhiteSpace(u) ? "" : " " + u)}";

    async Task<string> WhoAsync(CancellationToken ct) => await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? current.UserName ?? "";

    /// <summary>The template of an order: the one its spec was entered with, otherwise its product family's. Null for other types or families with none.</summary>
    public async Task<TestTemplate?> TemplateForAsync(SequflohTrackingRecord r, CancellationToken ct)
    {
        if (r.Type != TrackingType.Test) return null;
        var saved = await db.SequflohTestSpecs.AsNoTracking().Where(s => s.TrackingRecordId == r.Id).Select(s => new { s.TemplateKey, s.CustomFields }).FirstOrDefaultAsync(ct);
        var baseT = saved is not null ? SequflohTestTemplates.Find(saved.TemplateKey) : await FamilyTemplateAsync(r, ct);
        baseT ??= SequflohTestTemplates.Find(SequflohTestTemplates.OrderOnly)!;
        var added = ReadCustom(saved?.CustomFields).Where(c => c.RemovedAt is null).Select(c => new TestTemplateField(c.Key, c.Label, c.DefaultUnit));
        return baseT with { Fields = [.. baseT.Fields, .. added] };
    }

    async Task<TestTemplate?> FamilyTemplateAsync(SequflohTrackingRecord r, CancellationToken ct)
    {
        if (r.FamilyId is null) return null;
        var key = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.Id == r.FamilyId && v.ListKey == SequflohCatalog.ProductFamilies).Select(v => v.TestTemplate).FirstOrDefaultAsync(ct);
        return SequflohTestTemplates.Find(key);
    }

    async Task<(SequflohTrackingRecord R, TestTemplate T)> TestOrderAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var t = await TemplateForAsync(r, ct) ?? throw new AppException("No test fields are set up for this work order's product family.");
        return (r, t);
    }

    static decimal? Parse(string? s, string label)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) throw new AppException($"{label} must be a number.");
        return d;
    }

    static string? Unit(string? u) => string.IsNullOrWhiteSpace(u) ? null : u.Trim()[..Math.Min(u.Trim().Length, 20)];

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var t = await TemplateForAsync(r, ct);
        if (t is null) return new { Template = (object?)null, r.FamilyName };
        var spec = await db.SequflohTestSpecs.AsNoTracking().FirstOrDefaultAsync(s => s.TrackingRecordId == id, ct);
        var sv = spec is null ? [] : Read(spec.Values);
        var parts = await db.SequflohTestPartResults.IgnoreQueryFilters().AsNoTracking().Where(p => p.TrackingRecordId == id).OrderBy(p => p.PartIndex).ToListAsync(ct);
        return new
        {
            Template = new { t.Key, t.Label, Fields = t.Fields.Select(x => new { x.Key, x.Label, x.DefaultUnit, Added = x.Key.StartsWith("u-") }) },
            AddedFields = ReadCustom(spec?.CustomFields).Select(c => new { c.Key, c.Label, c.DefaultUnit, c.AddedBy, c.AddedAt, c.RemovedAt, c.RemovedBy, c.RemoveReason }),
            r.FamilyName,
            Spec = t.Fields.Select(x => { var v = sv.FirstOrDefault(s => s.Key == x.Key); return new { x.Key, x.Label, Value = ST(v), Min = v?.Text is null ? null : v.Min, Max = v?.Text is null ? null : v.Max, Strict = v?.Strict == true, Unit = v?.Unit ?? x.DefaultUnit }; }),
            SpecSaved = spec is not null, SpecUpdatedAt = spec?.UpdatedAt ?? spec?.CreatedAt,
            Parts = parts.Where(p => p.RemovedAt is null).Select(p => { var pv = Read(p.Values); return new { p.Id, p.PartIndex, p.Note, p.RecordedBy, RecordedAt = p.CreatedAt, p.EditedAt, p.EditedBy,
                Values = t.Fields.Select(x => new { x.Key, Value = N(pv.FirstOrDefault(v => v.Key == x.Key)?.Value) }) }; }),
            Removed = parts.Where(p => p.RemovedAt is not null).Select(p => new { p.PartIndex, p.RemovedAt, p.RemovedBy, p.RemoveReason }),
            Open = !SequflohTrackingService.IsClosed(r),
            CanCorrect = await m.CanAsync(Permissions.SequflohCorrectTestResults, r.SiteId, r.DepartmentId, ct),
            CanRemove = await m.CanAsync(Permissions.SequflohRemoveTestResults, r.SiteId, r.DepartmentId, ct),
        };
    }

    /// <summary>Saves the drawing spec. Changing a value or unit that was already entered needs the correct permission.</summary>
    public async Task SaveSpecAsync(Guid id, TestSpecInput x, CancellationToken ct)
    {
        var (r, t) = await TestOrderAsync(id, ct);
        var spec = await db.SequflohTestSpecs.FirstOrDefaultAsync(s => s.TrackingRecordId == id, ct);
        var old = spec is null ? [] : Read(spec.Values);
        var next = new List<Val>(); var lines = new List<string>(); var corrected = false;
        foreach (var fd in t.Fields)
        {
            var inp = x.Values?.FirstOrDefault(v => v.Key == fd.Key);
            var was = old.FirstOrDefault(v => v.Key == fd.Key);
            var unit = inp is null ? was?.Unit ?? fd.DefaultUnit : Unit(inp.Unit) ?? fd.DefaultUnit;
            var sp = inp is null ? null : SpecOf(inp.Value, $"Spec {fd.Label.ToLowerInvariant()}");
            var nv = inp is null ? (was is null ? new Val(fd.Key, null, unit) : was with { Unit = unit }) : new Val(fd.Key, sp?.Value, unit, sp?.Text, sp?.Min, sp?.Max, sp?.Strict == true ? true : null);
            // an unchanged value is stored exactly as before
            if (was is not null && ST(was) == ST(nv)) nv = was with { Unit = unit };
            next.Add(nv);
            var wasUnit = was?.Unit ?? fd.DefaultUnit;
            var (wt, nt) = (ST(was), ST(nv));
            if (wt == nt && unit == wasUnit) continue;
            if (wt != "" || spec is not null && unit != wasUnit) corrected = true;
            lines.Add(wt == "" ? $"Spec {fd.Label.ToLowerInvariant()} set to {SpecUnit(nt, unit)}" : $"Spec {fd.Label.ToLowerInvariant()} changed from {SpecUnit(wt, wasUnit)} to {SpecUnit(nt, unit)}");
        }
        next.AddRange(old.Where(o => t.Fields.All(fd => fd.Key != o.Key)));
        if (lines.Count == 0) return;
        if (corrected) await m.RequireAsync(Permissions.SequflohCorrectTestResults, r.SiteId, r.DepartmentId, ct);
        else SequflohTrackingService.RequireOpen(r);
        var why = Reason(x.Reason);
        if (corrected && why is null) throw new AppException("Enter the reason for the correction.");
        spec ??= await SpecRowAsync(r, t, ct);
        spec.Values = Write(next);
        var who = await WhoAsync(ct);
        foreach (var l in lines) f.Audit(corrected ? "TestSpecCorrected" : "TestSpecSet", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: {l} by {who}", r.SiteId, why);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adds the next part's results: Part 1, Part 2, ... The number is one more than the highest ever used on the order, removed parts included.</summary>
    public async Task<object> AddPartAsync(Guid id, TestPartInput x, CancellationToken ct)
    {
        var (r, t) = await TestOrderAsync(id, ct);
        SequflohTrackingService.RequireOpen(r);
        if (t.Fields.Count == 0) throw new AppException("Add a spec field first.");
        var vals = t.Fields.Select(fd => new Val(fd.Key, Parse(x.Values?.FirstOrDefault(v => v.Key == fd.Key)?.Value, $"Actual {fd.Label.ToLowerInvariant()}"), null)).ToList();
        var units = await UnitsAsync(id, t, ct);
        var who = await WhoAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var next = (await db.SequflohTestPartResults.IgnoreQueryFilters().Where(p => p.TrackingRecordId == id).MaxAsync(p => (int?)p.PartIndex, ct) ?? 0) + 1;
        var p = new SequflohTestPartResult { TrackingRecordId = r.Id, TrackingNumber = r.Number, PartIndex = next, TemplateKey = t.Key, Values = Write(vals), Note = Note(x.Note), RecordedBy = who };
        db.SequflohTestPartResults.Add(p);
        f.Audit("TestPartAdded", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: Part {next} test results added by {who}", r.SiteId);
        foreach (var (fd, v) in t.Fields.Zip(vals))
            if (v.Value is not null) f.Audit("TestPartValueSet", SequflohTrackingService.EntityType, r.Id, $"{r.Number} Part {next}: Actual {fd.Label.ToLowerInvariant()} set to {WithUnit(v.Value, units[fd.Key])} by {who}", r.SiteId);
        if (p.Note is not null) f.Audit("TestPartNote", SequflohTrackingService.EntityType, r.Id, $"{r.Number} Part {next}: note set to \"{p.Note}\" by {who}", r.SiteId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { p.Id, p.PartIndex };
    }

    async Task<Dictionary<string, string?>> UnitsAsync(Guid id, TestTemplate t, CancellationToken ct)
    {
        var json = await db.SequflohTestSpecs.AsNoTracking().Where(s => s.TrackingRecordId == id).Select(s => s.Values).FirstOrDefaultAsync(ct);
        var sv = json is null ? [] : Read(json);
        return t.Fields.ToDictionary(fd => fd.Key, fd => sv.FirstOrDefault(v => v.Key == fd.Key)?.Unit ?? fd.DefaultUnit);
    }

    /// <summary>Updates one part's results. Filling blanks is open; changing an entered value or the note needs the correct permission and a reason.</summary>
    public async Task UpdatePartAsync(Guid partId, TestPartInput x, CancellationToken ct)
    {
        var p = await db.SequflohTestPartResults.FirstOrDefaultAsync(e => e.Id == partId, ct) ?? throw new NotFoundException("Part test results");
        var (r, t) = await TestOrderAsync(p.TrackingRecordId, ct);
        var old = Read(p.Values); var units = await UnitsAsync(r.Id, t, ct);
        var next = new List<Val>(); var lines = new List<string>(); var corrected = false;
        foreach (var fd in t.Fields)
        {
            var inp = x.Values?.FirstOrDefault(v => v.Key == fd.Key);
            var was = old.FirstOrDefault(v => v.Key == fd.Key)?.Value;
            var val = inp is null ? was : Parse(inp.Value, $"Actual {fd.Label.ToLowerInvariant()}");
            next.Add(new(fd.Key, val, null));
            if (val == was) continue;
            if (was is not null) corrected = true;
            lines.Add(was is null ? $"Actual {fd.Label.ToLowerInvariant()} set to {WithUnit(val, units[fd.Key])}" : $"Actual {fd.Label.ToLowerInvariant()} changed from {WithUnit(was, units[fd.Key])} to {WithUnit(val, units[fd.Key])}");
        }
        next.AddRange(old.Where(o => t.Fields.All(fd => fd.Key != o.Key)));
        var note = x.Note is null ? p.Note : Note(x.Note);
        if (note != p.Note) { if (p.Note is not null) corrected = true; lines.Add(p.Note is null ? $"note set to \"{note}\"" : $"note changed from \"{p.Note}\" to \"{note ?? ""}\""); }
        if (lines.Count == 0) return;
        var why = Reason(x.Reason);
        if (corrected)
        {
            await m.RequireAsync(Permissions.SequflohCorrectTestResults, r.SiteId, r.DepartmentId, ct);
            if (why is null) throw new AppException("Enter the reason for the correction.");
        }
        else SequflohTrackingService.RequireOpen(r);
        var who = await WhoAsync(ct);
        p.Values = Write(next); p.Note = note; p.EditedAt = DateTimeOffset.UtcNow; p.EditedBy = who;
        foreach (var l in lines) f.Audit(corrected ? "TestPartCorrected" : "TestPartValueSet", SequflohTrackingService.EntityType, r.Id, $"{r.Number} Part {p.PartIndex}: {l} by {who}", r.SiteId, why);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a part's results with a reason. The row is kept in history and its number is never reused.</summary>
    public async Task RemovePartAsync(Guid partId, TestRemoveInput x, CancellationToken ct)
    {
        var p = await db.SequflohTestPartResults.FirstOrDefaultAsync(e => e.Id == partId, ct) ?? throw new NotFoundException("Part test results");
        var r = await tracking.LoadAsync(p.TrackingRecordId, false, ct);
        await m.RequireAsync(Permissions.SequflohRemoveTestResults, r.SiteId, r.DepartmentId, ct);
        var why = Reason(x.Reason) ?? throw new AppException("Enter the reason for removing these results.");
        var who = await WhoAsync(ct);
        p.RemovedAt = DateTimeOffset.UtcNow; p.RemovedBy = who; p.RemoveReason = why;
        f.Audit("TestPartRemoved", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: Part {p.PartIndex} test results removed by {who}. Reason: {why}", r.SiteId, why);
        await db.SaveChangesAsync(ct);
    }

    async Task<SequflohTestSpec> SpecRowAsync(SequflohTrackingRecord r, TestTemplate t, CancellationToken ct)
    {
        var spec = await db.SequflohTestSpecs.FirstOrDefaultAsync(s => s.TrackingRecordId == r.Id, ct);
        if (spec is not null) return spec;
        var baseKey = (await FamilyTemplateAsync(r, ct))?.Key ?? SequflohTestTemplates.OrderOnly;
        spec = new SequflohTestSpec { TrackingRecordId = r.Id, TrackingNumber = r.Number, TemplateKey = baseKey };
        db.SequflohTestSpecs.Add(spec);
        return spec;
    }

    static string Label(string? s) => string.IsNullOrWhiteSpace(s) ? throw new AppException("Enter the spec field name.") : s.Trim()[..Math.Min(s.Trim().Length, 80)];

    /// <summary>Adds a spec field to this order only (the family template is not changed). Open to anyone working the open order. It shows in the spec and in every part's results.</summary>
    public async Task AddFieldAsync(Guid id, TestFieldInput x, CancellationToken ct)
    {
        var (r, t) = await TestOrderAsync(id, ct);
        SequflohTrackingService.RequireOpen(r);
        var label = Label(x.Label); var unit = Unit(x.Unit) ?? "";
        if (t.Fields.Any(fd => string.Equals(fd.Label, label, StringComparison.OrdinalIgnoreCase))) throw new ConflictException($"{label} is already a spec field on this order.");
        var val = SpecOf(x.Value, $"Spec {label.ToLowerInvariant()}");
        var spec = await SpecRowAsync(r, t, ct);
        var who = await WhoAsync(ct);
        var list = ReadCustom(spec.CustomFields);
        var key = "u-" + Guid.NewGuid().ToString("N")[..10];
        list.Add(new(key, label, unit, who, DateTimeOffset.UtcNow));
        spec.CustomFields = JsonSerializer.Serialize(list, J);
        f.Audit("TestFieldAdded", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: spec field \"{label}\"{(unit == "" ? "" : $" ({unit})")} added to this order by {who}", r.SiteId);
        if (val is not null)
        {
            var nv = new Val(key, val.Value, unit == "" ? null : unit, val.Text, val.Min, val.Max, val.Strict ? true : null);
            var vals = Read(spec.Values); vals.Add(nv); spec.Values = Write(vals);
            f.Audit("TestSpecSet", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: Spec {label.ToLowerInvariant()} set to {SpecUnit(ST(nv), unit)} by {who}", r.SiteId);
        }
        await db.SaveChangesAsync(ct);
    }

    async Task<(SequflohTrackingRecord R, SequflohTestSpec S, List<Custom> L, int I)> CustomAsync(Guid id, string key, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        await m.RequireAsync(Permissions.SequflohCorrectTestResults, r.SiteId, r.DepartmentId, ct);
        var spec = await db.SequflohTestSpecs.FirstOrDefaultAsync(s => s.TrackingRecordId == id, ct) ?? throw new NotFoundException("Spec field");
        var list = ReadCustom(spec.CustomFields);
        var i = list.FindIndex(c => c.Key == key && c.RemovedAt is null);
        if (i < 0) throw new NotFoundException("Spec field");
        return (r, spec, list, i);
    }

    /// <summary>Renames a field added on the order, or changes its unit. Needs the correct test results permission and a reason.</summary>
    public async Task UpdateFieldAsync(Guid id, string key, TestFieldInput x, CancellationToken ct)
    {
        var (r, spec, list, i) = await CustomAsync(id, key, ct);
        var why = Reason(x.Reason) ?? throw new AppException("Enter the reason for the change.");
        var c = list[i]; var label = x.Label is null ? c.Label : Label(x.Label); var unit = x.Unit is null ? c.DefaultUnit : Unit(x.Unit) ?? "";
        if (label == c.Label && unit == c.DefaultUnit) return;
        if (label != c.Label && (await TemplateForAsync(r, ct))!.Fields.Any(fd => fd.Key != key && string.Equals(fd.Label, label, StringComparison.OrdinalIgnoreCase))) throw new ConflictException($"{label} is already a spec field on this order.");
        list[i] = c with { Label = label, DefaultUnit = unit };
        spec.CustomFields = JsonSerializer.Serialize(list, J);
        var who = await WhoAsync(ct);
        f.Audit("TestFieldChanged", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: spec field \"{c.Label}\" ({c.DefaultUnit}) changed to \"{label}\" ({unit}) by {who}. Reason: {why}", r.SiteId, why);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes a field added on the order. Soft: the field and any values entered stay in history. Needs the correct test results permission and a reason.</summary>
    public async Task RemoveFieldAsync(Guid id, string key, TestRemoveInput x, CancellationToken ct)
    {
        var (r, spec, list, i) = await CustomAsync(id, key, ct);
        var why = Reason(x.Reason) ?? throw new AppException("Enter the reason for removing this spec field.");
        var who = await WhoAsync(ct);
        list[i] = list[i] with { RemovedAt = DateTimeOffset.UtcNow, RemovedBy = who, RemoveReason = why };
        spec.CustomFields = JsonSerializer.Serialize(list, J);
        f.Audit("TestFieldRemoved", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: spec field \"{list[i].Label}\" removed from this order by {who}. Entered values are kept in history. Reason: {why}", r.SiteId, why);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Attaches a drawing picked by hand, for a test order whose part has no drawing linked. Audited.</summary>
    public async Task AttachDrawingAsync(Guid id, Guid drawingId, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        SequflohTrackingService.RequireOpen(r);
        var d = await db.Drawings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == drawingId && !x.IsArchived, ct) ?? throw new NotFoundException("Drawing");
        await m.RequireAsync(Permissions.SequflohViewDrawings, d.SiteId, d.DepartmentId, ct);
        if (await db.TrackingRecordDrawings.AnyAsync(a => a.TrackingRecordId == id && a.DrawingId == drawingId, ct)) return;
        db.TrackingRecordDrawings.Add(new Sequfloh.Domain.Drawings.TrackingRecordDrawing { TrackingRecordId = id, DrawingId = d.Id, AttachedRevision = d.CurrentRevision, AttachedAt = DateTimeOffset.UtcNow });
        var who = await WhoAsync(ct);
        f.Audit("DrawingAttached", SequflohTrackingService.EntityType, r.Id, $"{r.Number}: drawing {(string.IsNullOrEmpty(d.DrawingNumber) ? d.Title : d.DrawingNumber)} rev {d.CurrentRevision} attached by hand by {who}", r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    static string? Note(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 2000)];
    static string? Reason(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 500)];

    /// <summary>The print and job report blocks: the spec once, then "Part N test results" against the spec side by side. Blank actuals are marked.</summary>
    public async Task<List<PrintBlock>> PrintBlocksAsync(SequflohTrackingRecord r, Func<DateTimeOffset?, string> local, CancellationToken ct)
    {
        var t = await TemplateForAsync(r, ct);
        if (t is null) return [];
        var spec = await db.SequflohTestSpecs.AsNoTracking().FirstOrDefaultAsync(s => s.TrackingRecordId == r.Id, ct);
        var sv = spec is null ? [] : Read(spec.Values);
        var units = t.Fields.ToDictionary(fd => fd.Key, fd => sv.FirstOrDefault(v => v.Key == fd.Key)?.Unit ?? fd.DefaultUnit);
        string Spec(string k) => ST(sv.FirstOrDefault(v => v.Key == k)) is { Length: > 0 } d ? $"{d} {units[k]}".Trim() : "Not entered";
        var blocks = new List<PrintBlock> { new PrintHeading("Drawing spec"), new PrintFields([.. t.Fields.Select(fd => (fd.Label, (string?)Spec(fd.Key)))]) };
        var parts = await db.SequflohTestPartResults.AsNoTracking().Where(p => p.TrackingRecordId == r.Id).OrderBy(p => p.PartIndex).ToListAsync(ct);
        if (t.Fields.Count == 0 && parts.Count == 0) return [];
        if (parts.Count == 0) blocks.Add(new PrintParagraph("No part test results recorded."));
        foreach (var p in parts)
        {
            var pv = Read(p.Values);
            blocks.Add(new PrintHeading($"Part {p.PartIndex} test results"));
            blocks.Add(new PrintTable([("Test", 3), ("Spec", 2), ("Actual", 2)], [.. t.Fields.Select(fd => new string?[] { fd.Label, Spec(fd.Key),
                pv.FirstOrDefault(v => v.Key == fd.Key)?.Value is { } a ? $"{N(a)} {units[fd.Key]}".Trim() : "BLANK - not entered" })]));
            blocks.Add(new PrintParagraph($"Recorded by {p.RecordedBy}, {local(p.CreatedAt)}" + (p.EditedAt is null ? "" : $"; last changed by {p.EditedBy}, {local(p.EditedAt)}") + (p.Note is null ? "" : $". Note: {p.Note}")));
        }
        return blocks;
    }
}
