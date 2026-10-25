using Sequfloh.Tracking;
using Sequfloh.Infrastructure.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;
using Sequfloh.Domain.Security;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record PartReplaced(string PartNumber, string? Description, decimal PerAssembly, decimal? Quantity, decimal? UnitCost = null);
public sealed record CostLine(string Description, decimal Amount);
/// <summary>Report sections beyond the summary. All off means the summary only. <see cref="Tests"/> is the explicit "Include test results report?" choice:
/// when it is No, nothing about test results appears anywhere on the report, Full detail included.</summary>
public sealed record ReportOptions(bool PerTech = false, bool TimeLog = false, bool Findings = false, bool Tests = false, bool Attachments = false, bool Photos = false, bool Cost = false)
{
    public static readonly ReportOptions Full = new(true, true, true, true, true, true, true);
}
/// <summary>Findings / nonconformance for a supplier chargeback. Every field is optional.</summary>
public sealed record JobFindings(string? DiscoveryMethod = null, DateOnly? DateDiscovered = null, string? DiscoveredBy = null, string? Site = null, string? Supplier = null,
    string? SupplierPartNumber = null, string? LotDateCodes = null, string? PurchaseOrder = null, int? QtyInspected = null, int? QtyNonconforming = null, int? QtyAffected = null,
    string? Description = null, string? WarrantyClaim = null, string? AlertRecall = null, string? ErpReference = null, string? RepairMethod = null, string? RootCause = null);
public sealed record TestColumnMap(string? ResultColumn, List<string>? PassValues, List<string>? ValueColumns, string? BatchColumn, string? DateColumn);
public sealed record QuantityInput(int? Completed, int? NotRepaired, int? Target, bool ClearTarget = false, string? Note = null);
public sealed record KeyValue(string Column, decimal Min, decimal Average, decimal Max, int Count);

/// <summary>
/// Job-level quantity, parts replaced and tester result files for a work order, and the chargeback job report.
/// Quantity belongs to the whole job, not to a person. Anyone who can see the work order can update it; every change is audited.
/// </summary>
public sealed class SequflohJobService(AppDbContext db, ModuleSupport m, ICurrentUser current, SequflohTrackingService tracking, SequflohFoundationService f, IFileStorage storage)
{
    public static readonly string[] DefaultPassValues = ["PASS", "PASSED", "P", "OK", "GOOD", "TRUE", "1"];
    const long MaxFile = 25 * 1024 * 1024;
    static readonly string[] Allowed = [".csv", ".xlsx", ".pdf", ".txt"];
    static readonly string[] Photo = [".jpg", ".jpeg", ".png"];
    static readonly string[] Attach = [".csv", ".xlsx", ".pdf", ".txt", ".docx", ".jpg", ".jpeg", ".png"];
    public static ReportOptions Options(string json) => JsonSerializer.Deserialize<ReportOptions>(json, SequflohJson.Options) ?? new();
    public static JobFindings FindingsOf(string json) => JsonSerializer.Deserialize<JobFindings>(json, SequflohJson.Options) ?? new();
    static List<CostLine> Lines(string json) => JsonSerializer.Deserialize<List<CostLine>>(json, SequflohJson.Options) ?? [];

    public async Task SaveOptionsAsync(Guid id, ReportOptions o, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, true, ct);
        r.ReportOptions = JsonSerializer.Serialize(o, SequflohJson.Options);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveFindingsAsync(Guid id, JobFindings x, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
        if (x.QtyInspected is < 0 || x.QtyNonconforming is < 0 || x.QtyAffected is < 0) throw new AppException("A quantity can't be negative.");
        var clean = new JobFindings(T(x.DiscoveryMethod, 100), x.DateDiscovered, T(x.DiscoveredBy, 200), T(x.Site, 200), T(x.Supplier, 200), T(x.SupplierPartNumber, 100), T(x.LotDateCodes, 300),
            T(x.PurchaseOrder, 100), x.QtyInspected, x.QtyNonconforming, x.QtyAffected, T(x.Description, 4000), T(x.WarrantyClaim, 100), T(x.AlertRecall, 100), T(x.ErpReference, 100),
            T(x.RepairMethod, 4000), T(x.RootCause, 4000));
        if (clean.DiscoveryMethod is { } dm) await f.RequireOptionAsync("discovery-methods", dm, "Discovery method", ct);
        r.Findings = JsonSerializer.Serialize(clean, SequflohJson.Options);
        f.Audit("FindingsUpdated", SequflohTrackingService.EntityType, r.Id, $"Findings on {r.Number} updated", r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveCostLinesAsync(Guid id, List<CostLine> lines, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        var clean = lines.Where(l => !string.IsNullOrWhiteSpace(l.Description)).Select(l => new CostLine(l.Description.Trim()[..Math.Min(l.Description.Trim().Length, 200)], Math.Round(l.Amount, 2))).ToList();
        if (clean.Count > 50) throw new AppException("Up to 50 cost lines.");
        r.CostLines = JsonSerializer.Serialize(clean, SequflohJson.Options);
        f.Audit("CostLinesUpdated", SequflohTrackingService.EntityType, r.Id, $"Other cost lines on {r.Number}: {(clean.Count == 0 ? "none" : string.Join(", ", clean.Select(c => $"{c.Description} {c.Amount.ToString("0.00", CultureInfo.InvariantCulture)}")))}", r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    static List<PartReplaced> Parts(string json) => JsonSerializer.Deserialize<List<PartReplaced>>(json, SequflohJson.Options) ?? [];
    public static TestColumnMap Map(string json) => JsonSerializer.Deserialize<TestColumnMap>(json, SequflohJson.Options) ?? new(null, null, null, null, null);

    async Task<SequflohTrackingRecord> WorkableAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, true, ct);
        SequflohTrackingService.RequireOpen(r);
        return r;
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var files = await db.SequflohTestFiles.AsNoTracking().Where(x => x.TrackingRecordId == id).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return new
        {
            r.QuantityTarget, r.QuantityCompleted, r.QuantityNotRepaired,
            Parts = Parts(r.PartsReplaced).Select(p => new { p.PartNumber, p.Description, p.PerAssembly, p.Quantity, p.UnitCost, Total = Total(p, r.QuantityCompleted) }),
            Columns = Map(r.TestColumns),
            Files = files.Select(FileDto),
            Tests = new { Tested = files.Where(x => x.Kind == "test").Sum(x => x.Tested ?? 0), Passed = files.Where(x => x.Kind == "test").Sum(x => x.Passed ?? 0), Failed = files.Where(x => x.Kind == "test").Sum(x => x.Failed ?? 0) },
            Findings = FindingsOf(r.Findings), CostLines = Lines(r.CostLines), ReportOptions = Options(r.ReportOptions),
        };
    }

    static decimal Total(PartReplaced p, int completed) => p.Quantity ?? p.PerAssembly * completed;
    static object FileDto(SequflohTestFile x) => new
    {
        x.Id, x.Kind, x.FileName, x.SizeBytes, x.Batch, x.TestDate, UploadedAt = x.CreatedAt, x.Tested, x.Passed, x.Failed,
        KeyValues = JsonSerializer.Deserialize<List<KeyValue>>(x.KeyValues, SequflohJson.Options),
    };

    /// <summary>Adds to (or corrects, with negative numbers) the job's completed and could-not-repair quantity, or sets the target.</summary>
    public async Task UpdateQuantityAsync(Guid id, QuantityInput q, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        int c = r.QuantityCompleted + (q.Completed ?? 0), nr = r.QuantityNotRepaired + (q.NotRepaired ?? 0);
        if (c < 0 || nr < 0) throw new AppException("A quantity can't go below zero.");
        if (q.Target is < 0) throw new AppException("The target can't be negative.");
        var words = new List<string>();
        void Say(string label, int? d) { if (d is { } v && v != 0) words.Add($"{label} {(v > 0 ? "+" : "")}{v}"); }
        Say("completed", q.Completed); Say("could not repair", q.NotRepaired);
        if (q.ClearTarget) { r.QuantityTarget = null; words.Add("target cleared"); }
        else if (q.Target is { } t && t != r.QuantityTarget) { r.QuantityTarget = t; words.Add($"target set to {t}"); }
        if (words.Count == 0) return;
        r.QuantityCompleted = c; r.QuantityNotRepaired = nr;
        var note = string.IsNullOrWhiteSpace(q.Note) ? "" : $" ({q.Note.Trim()[..Math.Min(q.Note.Trim().Length, 200)]})";
        f.Audit("QuantityUpdated", SequflohTrackingService.EntityType, r.Id,
            $"Quantity on {r.Number}: {string.Join(", ", words)}. Now {c} completed{(r.QuantityTarget is { } tt ? $" of {tt}" : "")}, {nr} could not be repaired{note}", r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPartsAsync(Guid id, List<PartReplaced> parts, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        var clean = new List<PartReplaced>();
        foreach (var p in parts)
        {
            var pn = p.PartNumber?.Trim() ?? "";
            if (pn.Length == 0) continue;
            if (pn.Length > 100) throw new AppException("A part number can be at most 100 characters.");
            if (p.PerAssembly < 0 || p.Quantity < 0 || p.UnitCost < 0) throw new AppException($"Quantities for {pn} can't be negative.");
            if (clean.Any(x => string.Equals(x.PartNumber, pn, StringComparison.OrdinalIgnoreCase))) throw new AppException($"{pn} is listed twice.");
            clean.Add(new PartReplaced(pn, string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim(), p.PerAssembly, p.Quantity, p.UnitCost is { } uc ? Math.Round(uc, 4) : null));
        }
        r.PartsReplaced = JsonSerializer.Serialize(clean, SequflohJson.Options);
        f.Audit("PartsReplacedUpdated", SequflohTrackingService.EntityType, r.Id,
            $"Parts replaced on {r.Number}: {(clean.Count == 0 ? "none" : string.Join(", ", clean.Select(p => $"{p.PartNumber} x{p.PerAssembly.ToString("0.##", CultureInfo.InvariantCulture)} per assembly{(p.Quantity is { } qq ? $" (total set to {qq.ToString("0.##", CultureInfo.InvariantCulture)})" : "")}{(p.UnitCost is { } u ? $" at {u.ToString("0.00##", CultureInfo.InvariantCulture)} each" : "")}")))}", r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    // ---- tester files ----

    /// <summary>Reads the header and rows of a CSV or XLSX file; null for other files.</summary>
    public static (List<string> Headers, List<string[]> Rows)? ReadTable(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".csv")
        {
            using var ms = new MemoryStream(bytes);
            var a = CsvParser.Analyze(ms, null);
            ms.Position = 0;
            using var reader = new StreamReader(ms, CsvParser.EncodingFor(a.Encoding), detectEncodingFromByteOrderMarks: true);
            var all = CsvParser.Read(reader, a.Delimiter).Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
            if (all.Count == 0) return null;
            return (all[0].Select(h => h.Trim()).ToList(), all.Skip(1).ToList());
        }
        if (ext == ".xlsx")
        {
            using var ms = new MemoryStream(bytes);
            using var wb = new XLWorkbook(ms);
            var ws = wb.Worksheets.First();
            var used = ws.RangeUsed();
            if (used is null) return null;
            var rows = used.RowsUsed().Select(r => Enumerable.Range(1, used.ColumnCount()).Select(c => r.Cell(c).GetFormattedString().Trim()).ToArray()).ToList();
            if (rows.Count == 0) return null;
            return (rows[0].ToList(), rows.Skip(1).ToList());
        }
        return null;
    }

    static int Col(List<string> headers, string? name) => name is null ? -1 : headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The result column: the mapped one, else the first header that looks like a result.</summary>
    static int ResultCol(List<string> headers, TestColumnMap map)
    {
        var i = Col(headers, map.ResultColumn);
        if (i >= 0) return i;
        if (map.ResultColumn is not null) return -1;
        foreach (var w in new[] { "result", "pass/fail", "outcome", "status", "verdict", "pass" })
            if ((i = headers.FindIndex(h => h.Contains(w, StringComparison.OrdinalIgnoreCase))) >= 0) return i;
        return -1;
    }

    /// <summary>Counts pass and fail rows and summarises key value columns, using the job's remembered columns.</summary>
    public static void Parse(SequflohTestFile file, byte[] bytes, TestColumnMap map)
    {
        file.Tested = file.Passed = file.Failed = null; file.KeyValues = "[]";
        var t = ReadTable(bytes, file.FileName);
        if (t is not { } table) return;
        var rc = ResultCol(table.Headers, map);
        if (rc < 0) return;
        var pass = (map.PassValues is { Count: > 0 } pv ? pv : [.. DefaultPassValues]).Select(v => v.Trim().ToUpperInvariant()).ToHashSet();
        int tested = 0, passed = 0;
        foreach (var row in table.Rows)
        {
            var v = rc < row.Length ? row[rc].Trim() : "";
            if (v.Length == 0) continue;
            tested++;
            if (pass.Contains(v.ToUpperInvariant())) passed++;
        }
        file.Tested = tested; file.Passed = passed; file.Failed = tested - passed;
        var kv = new List<KeyValue>();
        foreach (var name in map.ValueColumns ?? [])
        {
            var c = Col(table.Headers, name);
            if (c < 0) continue;
            var nums = table.Rows.Select(r => c < r.Length && decimal.TryParse(r[c], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null).Where(d => d is not null).Select(d => d!.Value).ToList();
            if (nums.Count > 0) kv.Add(new KeyValue(table.Headers[c], nums.Min(), Math.Round(nums.Average(), 4), nums.Max(), nums.Count));
        }
        file.KeyValues = JsonSerializer.Serialize(kv, SequflohJson.Options);
        if (file.Batch is null && Col(table.Headers, map.BatchColumn) is var bc and >= 0)
            file.Batch = table.Rows.Select(r => bc < r.Length ? r[bc].Trim() : "").FirstOrDefault(v => v.Length > 0) is { } b ? b[..Math.Min(b.Length, 100)] : null;
        if (file.TestDate is null && Col(table.Headers, map.DateColumn) is var dc and >= 0)
            foreach (var r in table.Rows)
                if (dc < r.Length && DateTime.TryParse(r[dc], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) { file.TestDate = DateOnly.FromDateTime(dt); break; }
    }

    public async Task<List<object>> UploadAsync(Guid id, IReadOnlyList<(string Name, string Type, byte[] Bytes)> files, string? batch, DateOnly? testDate, CancellationToken ct, string kind = "test")
    {
        var r = await WorkableAsync(id, ct);
        if (kind is not ("test" or "attachment" or "evidence")) throw new AppException("Unknown file kind.");
        var allowed = kind == "evidence" ? Photo : kind == "attachment" ? Attach : Allowed;
        if (files.Count == 0) throw new AppException("Choose one or more result files.");
        var map = Map(r.TestColumns);
        var added = new List<SequflohTestFile>();
        var who = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? current.UserName ?? "";
        foreach (var (name, type, bytes) in files)
        {
            var safe = Path.GetFileName(name);
            if (!allowed.Contains(Path.GetExtension(safe).ToLowerInvariant()))
                throw new AppException(kind == "evidence" ? $"{safe}: photos must be JPG or PNG." : kind == "attachment" ? $"{safe}: attachments must be PDF, Word, Excel, CSV, text, JPG or PNG." : $"{safe}: result files must be CSV, XLSX, PDF or TXT.");
            if (bytes.Length > MaxFile) throw new AppException($"{safe} is larger than 25 MB.");
            var x = new SequflohTestFile
            {
                TrackingRecordId = r.Id, TrackingNumber = r.Number, FileName = safe.Length > 300 ? safe[^300..] : safe, ContentType = string.IsNullOrEmpty(type) ? "application/octet-stream" : type,
                SizeBytes = bytes.Length, Batch = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim()[..Math.Min(batch.Trim().Length, 100)], TestDate = testDate, UploadedByName = who, Kind = kind,
            };
            x.StorageKey = $"sequfloh/test-files/{r.Id:N}/{x.Id:N}{Path.GetExtension(safe).ToLowerInvariant()}";
            if (kind == "test") try { Parse(x, bytes, map); } catch (Exception) { x.Tested = x.Passed = x.Failed = null; } // an unreadable file is still kept
            using (var s = new MemoryStream(bytes)) await storage.PutAsync(x.StorageKey, s, x.ContentType, ct);
            db.SequflohTestFiles.Add(x);
            added.Add(x);
        }
        await db.SaveChangesAsync(ct);
        return added.Select(FileDto).ToList();
    }

    async Task<(SequflohTestFile File, SequflohTrackingRecord Record)> FileAsync(Guid fileId, bool track, CancellationToken ct)
    {
        var q = track ? db.SequflohTestFiles : db.SequflohTestFiles.AsNoTracking();
        var x = await q.FirstOrDefaultAsync(e => e.Id == fileId, ct) ?? throw new NotFoundException("Test file");
        return (x, await tracking.LoadAsync(x.TrackingRecordId, track, ct));
    }

    async Task<byte[]> BytesAsync(SequflohTestFile x, CancellationToken ct)
    {
        await using var s = await storage.GetAsync(x.StorageKey, ct) ?? throw new NotFoundException("Test file content");
        using var ms = new MemoryStream(); await s.CopyToAsync(ms, ct); return ms.ToArray();
    }

    public async Task<(byte[] Bytes, string Name, string Type)> DownloadAsync(Guid fileId, CancellationToken ct)
    {
        var (x, _) = await FileAsync(fileId, false, ct);
        return (await BytesAsync(x, ct), x.FileName, x.ContentType);
    }

    public async Task<object> ColumnsAsync(Guid fileId, CancellationToken ct)
    {
        var (x, r) = await FileAsync(fileId, false, ct);
        var t = ReadTable(await BytesAsync(x, ct), x.FileName);
        var headers = t?.Headers ?? [];
        var map = Map(r.TestColumns);
        var rc = ResultCol(headers, map);
        return new { Headers = headers, Sample = t?.Rows.Take(5) ?? [], Suggested = rc >= 0 ? headers[rc] : null, Current = map };
    }

    /// <summary>Remembers how this job's result files are read and re-reads every CSV and XLSX file on the job.</summary>
    public async Task SaveColumnsAsync(Guid id, TestColumnMap map, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var clean = new TestColumnMap(T(map.ResultColumn), map.PassValues?.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList(),
            map.ValueColumns?.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList(), T(map.BatchColumn), T(map.DateColumn));
        r.TestColumns = JsonSerializer.Serialize(clean, SequflohJson.Options);
        foreach (var x in await db.SequflohTestFiles.Where(e => e.TrackingRecordId == id && e.Kind == "test").ToListAsync(ct))
        {
            if (Path.GetExtension(x.FileName).ToLowerInvariant() is not (".csv" or ".xlsx")) continue;
            try { Parse(x, await BytesAsync(x, ct), clean); } catch (Exception) { x.Tested = x.Passed = x.Failed = null; }
        }
        f.Audit("TestColumnsUpdated", SequflohTrackingService.EntityType, r.Id,
            $"Test result columns on {r.Number}: result \"{clean.ResultColumn ?? "auto"}\", pass values {string.Join("/", clean.PassValues ?? [.. DefaultPassValues])}" +
            (clean.ValueColumns is { Count: > 0 } v ? $", key values {string.Join(", ", v)}" : ""), r.SiteId);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteFileAsync(Guid fileId, CancellationToken ct)
    {
        var (x, r) = await FileAsync(fileId, true, ct);
        SequflohTrackingService.RequireOpen(r);
        db.SequflohTestFiles.Remove(x);
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(x.StorageKey, ct);
    }

    // ---- report ----

    public sealed record ReportPerson(string Name, long Seconds, long PausedSeconds = 0, int Sessions = 0);
    public sealed record ReportTimeEvent(string Name, string Kind, DateTimeOffset At);
    public sealed record ReportPart(string PartNumber, string? Description, decimal PerAssembly, decimal Quantity, bool Overridden, decimal? UnitCost = null);
    public sealed record ReportFile(string FileName, string? Batch, DateOnly? TestDate, int? Tested, int? Passed, int? Failed, List<KeyValue> KeyValues, DateTimeOffset UploadedAt);
    public sealed record ReportAttachment(Guid Id, string FileName, string Kind, DateTimeOffset UploadedAt);
    public sealed record JobReport(Guid Id, string Number, string Type, string Status, string Title, string? PartNumber, string? PartDescription, string? WorkOrder,
        int? QuantityTarget, int QuantityCompleted, int QuantityNotRepaired, List<ReportPerson> People, long TotalSeconds,
        List<ReportPart> Parts, decimal PartsTotal, List<ReportFile> Files, int Tested, int Passed, int Failed, DateTimeOffset GeneratedAt,
        long PausedSeconds = 0, List<ReportTimeEvent>? TimeLog = null, JobFindings? Findings = null, List<CostLine>? CostLines = null, ReportOptions? Options = null,
        List<ReportAttachment>? Attachments = null, List<ReportAttachment>? Photos = null, string? WorkInstructionNumber = null, string? WorkInstructionRevision = null,
        decimal PartsCost = 0, decimal OtherCost = 0);

    public async Task<JobReport> ReportAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var now = m.Now;
        var entries = await db.SequflohTimeEntries.AsNoTracking().Where(e => e.TrackingRecordId == id).ToListAsync(ct);
        var people = entries.GroupBy(e => e.UserId).Select(g => new ReportPerson(g.OrderBy(e => e.StartedAt).Last().UserName,
            g.Sum(e => SequflohTimeService.Worked(e, now)), g.Sum(e => SequflohTimeService.Paused(e, now)), g.Count())).OrderBy(p => p.Name).ToList();
        var log = entries.SelectMany(e => SequflohTimeService.EventsOf(e).Select(v => new ReportTimeEvent(e.UserName, SequflohTimeService.Label(v), v.At))).OrderBy(v => v.At).ToList();
        var parts = Parts(r.PartsReplaced).Select(p => new ReportPart(p.PartNumber, p.Description, p.PerAssembly, Total(p, r.QuantityCompleted), p.Quantity is not null, p.UnitCost)).ToList();
        var all = await db.SequflohTestFiles.AsNoTracking().Where(x => x.TrackingRecordId == id).ToListAsync(ct);
        var opts = Options(r.ReportOptions);
        // "Include test results report?" No: the report carries no test results at all
        var files = !opts.Tests ? [] : all.Where(x => x.Kind == "test")
            .OrderBy(x => x.TestDate ?? DateOnly.FromDateTime(x.CreatedAt.UtcDateTime)).ThenBy(x => x.Batch).ThenBy(x => x.CreatedAt)
            .Select(x => new ReportFile(x.FileName, x.Batch, x.TestDate, x.Tested, x.Passed, x.Failed, JsonSerializer.Deserialize<List<KeyValue>>(x.KeyValues, SequflohJson.Options) ?? [], x.CreatedAt)).ToList();
        ReportAttachment A(SequflohTestFile x) => new(x.Id, x.FileName, x.Kind, x.CreatedAt);
        var lines = Lines(r.CostLines);
        return new JobReport(r.Id, r.Number, r.Type.ToString(), r.Status.ToString(), r.WorkInstructionTitle, r.PartNumber, r.PartDescription, r.WorkOrder,
            r.QuantityTarget, r.QuantityCompleted, r.QuantityNotRepaired, people, people.Sum(p => p.Seconds), parts, parts.Sum(p => p.Quantity),
            files, files.Sum(x => x.Tested ?? 0), files.Sum(x => x.Passed ?? 0), files.Sum(x => x.Failed ?? 0), now,
            people.Sum(p => p.PausedSeconds), log, FindingsOf(r.Findings), lines, opts,
            [.. all.Where(x => x.Kind == "attachment").OrderBy(x => x.CreatedAt).Select(A)], [.. all.Where(x => x.Kind == "evidence").OrderBy(x => x.CreatedAt).Select(A)],
            r.WorkInstructionNumber, r.WorkInstructionRevision,
            parts.Sum(p => p.UnitCost is { } u ? Math.Round(u * p.Quantity, 2) : 0), lines.Sum(l => l.Amount));
    }

    public static string Hours(long seconds) => (seconds / 3600m).ToString("0.00", CultureInfo.InvariantCulture);
    static string N(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    static string Money(decimal d) => d.ToString("#,##0.00", CultureInfo.InvariantCulture);
    static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// The chargeback job report as a PDF. Supplier, reference and rate are typed in when printing and are not stored. The summary is always
    /// printed; each extra section prints only when it is chosen in the report options (or <paramref name="full"/>) and has something in it.
    /// </summary>
    public async Task<(byte[] Pdf, string Name)> PdfAsync(Guid id, string? supplier, string? reference, decimal? rate, SequflohPrintService print, CancellationToken ct, bool full = false)
    {
        var x = await ReportAsync(id, ct);
        var rec = await tracking.LoadAsync(id, false, ct);
        var saved = x.Options ?? new();
        var o = full ? ReportOptions.Full with { Tests = saved.Tests } : saved;
        var (printed, local) = await print.ClockAsync(rec.SiteId, ct);
        var org = await print.OrganizationAsync(ct);
        string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "____" : s.Trim();
        var hours = x.TotalSeconds / 3600m;
        decimal? labour = rate is { } rt ? Math.Round(hours * rt, 2) : null;
        var fd = x.Findings ?? new();
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Chargeback"),
            new PrintFields([("Supplier", Blank(supplier ?? fd.Supplier)), ("Our company", org), ("Supplier reference / PO", Blank(reference ?? fd.PurchaseOrder)), ("Report date", local(x.GeneratedAt))]),
            new PrintHeading("Job"),
            new PrintFields([("Work order", x.WorkOrder ?? x.Number), ("Record", x.Number), ("Job type", x.Type), ("Work instruction", (string.IsNullOrEmpty(x.WorkInstructionNumber) ? "—" : $"{x.WorkInstructionNumber} rev {x.WorkInstructionRevision} {x.Title}").Trim()),
                ("Assembly part number", x.PartNumber ?? "—"), ("Description", x.PartDescription ?? "—"),
                ("Quantity target", x.QuantityTarget?.ToString(CultureInfo.InvariantCulture)), ("Quantity repaired", x.QuantityCompleted.ToString(CultureInfo.InvariantCulture)),
                ("Could not repair", x.QuantityNotRepaired == 0 ? null : x.QuantityNotRepaired.ToString(CultureInfo.InvariantCulture)), ("Status", x.Status),
                ("Total man hours", Hours(x.TotalSeconds) + " (paused time excluded)")]),
        };
        if (x.Parts.Count > 0)
        {
            blocks.Add(new PrintHeading("Parts replaced"));
            blocks.Add(new PrintTable([("Part number", 2.5), ("Description", 3), ("Per assembly", 1.2), ("Assemblies", 1.2), ("Quantity replaced", 1.5)],
                [.. x.Parts.Select(p => new string?[] { p.PartNumber, p.Description ?? "", N(p.PerAssembly), x.QuantityCompleted.ToString(CultureInfo.InvariantCulture), N(p.Quantity) + (p.Overridden ? " (entered)" : "") }),
                 ["Total", "", "", "", N(x.PartsTotal)]]));
        }
        if (o.Findings && HasFindings(fd)) AddFindings(blocks, fd, x);
        if (o.PerTech && x.People.Count > 0)
        {
            blocks.Add(new PrintHeading("Man hours by technician"));
            blocks.Add(new PrintTable([("Technician", 3), ("Sessions", 1), ("Worked hours", 1.3), ("Paused hours (not counted)", 1.8)],
                [.. x.People.Select(p => new string?[] { p.Name, p.Sessions.ToString(CultureInfo.InvariantCulture), Hours(p.Seconds), Hours(p.PausedSeconds) }),
                 ["Total", x.People.Sum(p => p.Sessions).ToString(CultureInfo.InvariantCulture), Hours(x.TotalSeconds), Hours(x.PausedSeconds)]]));
        }
        if (o.TimeLog && x.TimeLog is { Count: > 0 } tl)
        {
            blocks.Add(new PrintHeading("Detailed time log"));
            blocks.Add(new PrintTable([("When", 2.2), ("Technician", 2.5), ("Event", 1.6)], [.. tl.Select(e => new string?[] { local(e.At), e.Name, e.Kind })]));
        }
        if (o.Tests && x.Files.Count > 0)
        {
            blocks.Add(new PrintHeading("Test results"));
            blocks.Add(new PrintFields([("Tested", x.Tested.ToString(CultureInfo.InvariantCulture)), ("Pass", x.Passed.ToString(CultureInfo.InvariantCulture)), ("Fail", x.Failed.ToString(CultureInfo.InvariantCulture)),
                ("Pass rate", x.Tested == 0 ? "—" : (x.Passed * 100m / x.Tested).ToString("0.0", CultureInfo.InvariantCulture) + "%")]));
            blocks.Add(new PrintTable([("Batch / date", 2), ("Tested", 1), ("Pass", 1), ("Fail", 1), ("Key values", 2.5), ("Source file", 2.5)],
                x.Files.Select(fl => new string?[]
                {
                    string.Join(" / ", new[] { fl.Batch, fl.TestDate?.ToString("yyyy-MM-dd") }.Where(s => s is not null)) is { Length: > 0 } bd ? bd : "—",
                    fl.Tested?.ToString(CultureInfo.InvariantCulture) ?? "not read", I(fl.Passed), I(fl.Failed),
                    string.Join("; ", fl.KeyValues.Select(k => $"{k.Column} {N(k.Min)}–{N(k.Max)} (avg {N(k.Average)})")), fl.FileName,
                }).ToList()));
        }
        if (o.Attachments && x.Attachments is { Count: > 0 } at)
        {
            blocks.Add(new PrintHeading("Attachments"));
            blocks.Add(new PrintTable([("File", 6), ("Added", 2)], [.. at.Select(a => new string?[] { a.FileName, local(a.UploadedAt) })]));
        }
        if (o.Photos && x.Photos is { Count: > 0 } ph)
        {
            blocks.Add(new PrintHeading("Photos and evidence"));
            var files = await db.SequflohTestFiles.AsNoTracking().Where(e => e.TrackingRecordId == id && e.Kind == "evidence").ToDictionaryAsync(e => e.Id, ct);
            foreach (var p in ph)
            {
                var bytes = await BytesAsync(files[p.Id], ct);
                var caption = $"{p.FileName} · {local(p.UploadedAt)}";
                if (PdfCanvas.JpegSize(bytes) is not null) blocks.Add(new PrintImage(bytes, caption));
                else blocks.Add(new PrintParagraph($"Photo attached separately: {caption}"));
            }
        }
        // chargeback summary: always the totals; the line-by-line breakdown when chosen
        blocks.Add(new PrintHeading("Chargeback summary"));
        if (o.Cost)
        {
            var rows = new List<string?[]> { new[] { "Labor", $"{Hours(x.TotalSeconds)} h × {(rate is { } r1 ? Money(r1) : "____")}", labour is { } l1 ? Money(l1) : "____" } };
            rows.AddRange(x.Parts.Where(p => p.UnitCost is not null).Select(p => new string?[] { $"Part {p.PartNumber}", $"{N(p.Quantity)} × {Money(p.UnitCost!.Value)}", Money(Math.Round(p.UnitCost!.Value * p.Quantity, 2)) }));
            rows.AddRange((x.CostLines ?? []).Select(cl => new string?[] { cl.Description, "", Money(cl.Amount) }));
            blocks.Add(new PrintTable([("Item", 3), ("Basis", 2.5), ("Amount", 1.5)], rows));
        }
        var total = (labour ?? 0) + x.PartsCost + x.OtherCost;
        blocks.Add(new PrintFields([("Labor", labour is { } l2 ? Money(l2) : "____ (computed when a rate is entered)"), ("Parts", x.PartsCost == 0 ? null : Money(x.PartsCost)),
            ("Other costs", x.OtherCost == 0 ? null : Money(x.OtherCost)), ("Total chargeback", labour is null && total == 0 ? "____" : Money(total) + (labour is null ? " (labor not included: no rate entered)" : ""))]));
        blocks.Add(new PrintHeading("Sign-off"));
        blocks.Add(new PrintFields([("Prepared by", "________________"), ("Date", "________"), ("Supplier acknowledgement", "________________")]));
        var header = new PrintHeader(org, $"Chargeback Report: {x.PartNumber ?? x.Title}", x.Number, null, x.Status, null, string.IsNullOrWhiteSpace(supplier) ? null : $"Supplier: {supplier.Trim()}",
            "Job report", current.UserName ?? "", printed, null);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("JobReportPrinted", SequflohTrackingService.EntityType, rec.Id, $"Printed the job report for {x.Number}", rec.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"Job Report RPT {Sequfloh.Imports.ImportedFileService.SafeFileName(x.Number)}.pdf");
    }

    /// <summary>Saves the job report PDF of the work order as it is now to the saved reports under Reports. Kept as saved; a later save adds a new one.</summary>
    public async Task<SequflohSavedJobReport> SaveReportAsync(Guid id, string reason, SequflohPrintService print, CancellationToken ct)
    {
        var rec = await tracking.LoadAsync(id, false, ct);
        var (pdf, name) = await PdfAsync(id, null, null, null, print, ct);
        var by = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? current.UserName ?? "";
        var x = new SequflohSavedJobReport { TrackingRecordId = rec.Id, TrackingNumber = rec.Number, WorkOrderNumber = rec.WorkOrder, SiteId = rec.SiteId, Reason = reason, FileName = name, Pdf = pdf, CreatedByName = by };
        db.SequflohSavedJobReports.Add(x);
        f.Audit("JobReportSaved", SequflohTrackingService.EntityType, rec.Id,
            reason == "Completed" ? $"{rec.Number}: job report saved to Reports when the work order was completed" : $"{rec.Number}: job report saved to Reports", rec.SiteId);
        await db.SaveChangesAsync(ct);
        return x;
    }

    /// <summary>Saved job reports and saved chargeback reports, newest first, searched by work order number (or record number) and filtered by type ("job" or "chargeback").</summary>
    public async Task<object> SavedReportsAsync(string? q, string? type, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohViewReports, ct)) throw new ForbiddenException();
        var like = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";
        var rows = new List<(string Type, Guid Id, Guid? TrackingId, string? WorkOrder, string? Number, string? Detail, DateTimeOffset At, string By)>();
        if (type is null or "" or "job")
        {
            var jq = db.SequflohSavedJobReports.AsNoTracking();
            if (like is not null) jq = jq.Where(x => EF.Functions.ILike(x.TrackingNumber, like) || (x.WorkOrderNumber != null && EF.Functions.ILike(x.WorkOrderNumber, like)));
            rows.AddRange((await jq.OrderByDescending(x => x.CreatedAt).Take(500).Select(x => new { x.Id, x.TrackingRecordId, x.WorkOrderNumber, x.TrackingNumber, x.Reason, x.CreatedAt, x.CreatedByName }).ToListAsync(ct))
                .Select(x => ("job", x.Id, x.TrackingRecordId, x.WorkOrderNumber, (string?)x.TrackingNumber, (string?)(x.Reason == "Completed" ? "Saved when completed" : "Saved from the job report"), x.CreatedAt, x.CreatedByName)));
        }
        if (type is null or "" or "chargeback")
        {
            var cq = db.SequflohChargebackReports.AsNoTracking();
            if (like is not null) cq = cq.Where(x => EF.Functions.ILike(x.WorkOrderNumber, like) || (x.TrackingNumber != null && EF.Functions.ILike(x.TrackingNumber, like)));
            rows.AddRange((await cq.OrderByDescending(x => x.CreatedAt).Take(500).Select(x => new { x.Id, x.TrackingRecordId, x.WorkOrderNumber, x.TrackingNumber, x.Supplier, x.CreatedAt, x.CreatedByName }).ToListAsync(ct))
                .Select(x => ("chargeback", x.Id, x.TrackingRecordId, (string?)x.WorkOrderNumber, x.TrackingNumber, x.Supplier, x.CreatedAt, x.CreatedByName)));
        }
        return rows.OrderByDescending(r => r.At).Take(500).Select(r => new
        {
            r.Type, r.Id, TrackingRecordId = r.TrackingId, WorkOrderNumber = r.WorkOrder, TrackingNumber = r.Number, r.Detail, CreatedAt = r.At, CreatedByName = r.By,
            Url = r.Type == "job" ? $"/api/sequfloh/saved-reports/job/{r.Id}/pdf" : $"/api/sequfloh/chargebacks/{r.Id}/pdf",
        });
    }

    public async Task<(byte[] Pdf, string Name)> SavedJobReportPdfAsync(Guid id, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohViewReports, ct)) throw new ForbiddenException();
        var x = await db.SequflohSavedJobReports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Saved job report");
        return (x.Pdf, x.FileName);
    }

    public static bool HasFindings(JobFindings f) => f with { ErpReference = null } != new JobFindings();

    static void AddFindings(List<PrintBlock> blocks, JobFindings fd, JobReport x)
    {
        blocks.Add(new PrintHeading("Findings / nonconformance"));
        blocks.Add(new PrintFields([("Discovery method", fd.DiscoveryMethod), ("Date discovered", fd.DateDiscovered?.ToString("yyyy-MM-dd")), ("Discovered by", fd.DiscoveredBy), ("Site", fd.Site),
            ("Supplier", fd.Supplier), ("Supplier part number", fd.SupplierPartNumber), ("Lot / date codes", fd.LotDateCodes), ("PO", fd.PurchaseOrder),
            ("Qty inspected", I(fd.QtyInspected)), ("Qty nonconforming", I(fd.QtyNonconforming)), ("Qty affected", I(fd.QtyAffected)),
            ("Warranty claim #", fd.WarrantyClaim), ("Alert / recall #", fd.AlertRecall)])); // the ERP reference stays on the Findings tab and is not printed
        if (fd.Description is { } d) blocks.Add(new PrintFields([("Description", d)]));
        if (fd.RepairMethod is not null || fd.RootCause is not null)
        {
            blocks.Add(new PrintHeading("Repair description"));
            blocks.Add(new PrintFields([("Repair method", fd.RepairMethod), ("Work instruction used", (string.IsNullOrEmpty(x.WorkInstructionNumber) ? "—" : $"{x.WorkInstructionNumber} rev {x.WorkInstructionRevision}")), ("Supplier root cause", fd.RootCause)]));
        }
    }
}
