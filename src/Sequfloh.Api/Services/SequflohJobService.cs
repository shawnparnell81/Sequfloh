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

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record PartReplaced(string PartNumber, string? Description, decimal PerAssembly, decimal? Quantity);
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

    static List<PartReplaced> Parts(string json) => JsonSerializer.Deserialize<List<PartReplaced>>(json, SequflohJson.Options) ?? [];
    public static TestColumnMap Map(string json) => JsonSerializer.Deserialize<TestColumnMap>(json, SequflohJson.Options) ?? new(null, null, null, null, null);

    async Task<SequflohTrackingRecord> WorkableAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, true, ct);
        if (r.Status == TrackingStatus.Canceled) throw new ConflictException($"{r.Number} is canceled.");
        return r;
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var files = await db.SequflohTestFiles.AsNoTracking().Where(x => x.TrackingRecordId == id).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return new
        {
            r.QuantityTarget, r.QuantityCompleted, r.QuantityNotRepaired,
            Parts = Parts(r.PartsReplaced).Select(p => new { p.PartNumber, p.Description, p.PerAssembly, p.Quantity, Total = Total(p, r.QuantityCompleted) }),
            Columns = Map(r.TestColumns),
            Files = files.Select(FileDto),
            Tests = new { Tested = files.Sum(x => x.Tested ?? 0), Passed = files.Sum(x => x.Passed ?? 0), Failed = files.Sum(x => x.Failed ?? 0) },
        };
    }

    static decimal Total(PartReplaced p, int completed) => p.Quantity ?? p.PerAssembly * completed;
    static object FileDto(SequflohTestFile x) => new
    {
        x.Id, x.FileName, x.SizeBytes, x.Batch, x.TestDate, x.UploadedByName, UploadedAt = x.CreatedAt, x.Tested, x.Passed, x.Failed,
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
            if (p.PerAssembly < 0 || p.Quantity < 0) throw new AppException($"Quantities for {pn} can't be negative.");
            if (clean.Any(x => string.Equals(x.PartNumber, pn, StringComparison.OrdinalIgnoreCase))) throw new AppException($"{pn} is listed twice.");
            clean.Add(new PartReplaced(pn, string.IsNullOrWhiteSpace(p.Description) ? null : p.Description.Trim(), p.PerAssembly, p.Quantity));
        }
        r.PartsReplaced = JsonSerializer.Serialize(clean, SequflohJson.Options);
        f.Audit("PartsReplacedUpdated", SequflohTrackingService.EntityType, r.Id,
            $"Parts replaced on {r.Number}: {(clean.Count == 0 ? "none" : string.Join(", ", clean.Select(p => $"{p.PartNumber} x{p.PerAssembly.ToString("0.##", CultureInfo.InvariantCulture)} per assembly{(p.Quantity is { } qq ? $" (total set to {qq.ToString("0.##", CultureInfo.InvariantCulture)})" : "")}")))}", r.SiteId);
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

    public async Task<List<object>> UploadAsync(Guid id, IReadOnlyList<(string Name, string Type, byte[] Bytes)> files, string? batch, DateOnly? testDate, CancellationToken ct)
    {
        var r = await WorkableAsync(id, ct);
        if (files.Count == 0) throw new AppException("Choose one or more result files.");
        var map = Map(r.TestColumns);
        var added = new List<SequflohTestFile>();
        var who = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? current.UserName ?? "";
        foreach (var (name, type, bytes) in files)
        {
            var safe = Path.GetFileName(name);
            if (!Allowed.Contains(Path.GetExtension(safe).ToLowerInvariant())) throw new AppException($"{safe}: result files must be CSV, XLSX, PDF or TXT.");
            if (bytes.Length > MaxFile) throw new AppException($"{safe} is larger than 25 MB.");
            var x = new SequflohTestFile
            {
                TrackingRecordId = r.Id, TrackingNumber = r.Number, FileName = safe.Length > 300 ? safe[^300..] : safe, ContentType = string.IsNullOrEmpty(type) ? "application/octet-stream" : type,
                SizeBytes = bytes.Length, Batch = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim()[..Math.Min(batch.Trim().Length, 100)], TestDate = testDate, UploadedByName = who,
            };
            x.StorageKey = $"sequfloh/test-files/{r.Id:N}/{x.Id:N}{Path.GetExtension(safe).ToLowerInvariant()}";
            try { Parse(x, bytes, map); } catch (Exception) { x.Tested = x.Passed = x.Failed = null; } // an unreadable file is still kept
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
        foreach (var x in await db.SequflohTestFiles.Where(e => e.TrackingRecordId == id).ToListAsync(ct))
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
        if (r.Status == TrackingStatus.Canceled) throw new ConflictException($"{r.Number} is canceled.");
        db.SequflohTestFiles.Remove(x);
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(x.StorageKey, ct);
    }

    // ---- report ----

    public sealed record ReportPerson(string Name, long Seconds);
    public sealed record ReportPart(string PartNumber, string? Description, decimal PerAssembly, decimal Quantity, bool Overridden);
    public sealed record ReportFile(string FileName, string? Batch, DateOnly? TestDate, int? Tested, int? Passed, int? Failed, List<KeyValue> KeyValues, string UploadedByName, DateTimeOffset UploadedAt);
    public sealed record JobReport(Guid Id, string Number, string Type, string Status, string Title, string? PartNumber, string? PartDescription, string? WorkOrder,
        int? QuantityTarget, int QuantityCompleted, int QuantityNotRepaired, List<ReportPerson> People, long TotalSeconds,
        List<ReportPart> Parts, decimal PartsTotal, List<ReportFile> Files, int Tested, int Passed, int Failed, DateTimeOffset GeneratedAt);

    public async Task<JobReport> ReportAsync(Guid id, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(id, false, ct);
        var now = m.Now;
        var entries = await db.SequflohTimeEntries.AsNoTracking().Where(e => e.TrackingRecordId == id).ToListAsync(ct);
        long Sec(SequflohTimeEntry e) => e.StoppedAt is null ? Math.Max(0, (long)(now - e.StartedAt).TotalSeconds) : e.Seconds;
        var people = entries.GroupBy(e => e.UserId).Select(g => new ReportPerson(g.OrderBy(e => e.StartedAt).Last().UserName, g.Sum(Sec))).OrderBy(p => p.Name).ToList();
        var parts = Parts(r.PartsReplaced).Select(p => new ReportPart(p.PartNumber, p.Description, p.PerAssembly, Total(p, r.QuantityCompleted), p.Quantity is not null)).ToList();
        var files = (await db.SequflohTestFiles.AsNoTracking().Where(x => x.TrackingRecordId == id).ToListAsync(ct))
            .OrderBy(x => x.TestDate ?? DateOnly.FromDateTime(x.CreatedAt.UtcDateTime)).ThenBy(x => x.Batch).ThenBy(x => x.CreatedAt)
            .Select(x => new ReportFile(x.FileName, x.Batch, x.TestDate, x.Tested, x.Passed, x.Failed, JsonSerializer.Deserialize<List<KeyValue>>(x.KeyValues, SequflohJson.Options) ?? [], x.UploadedByName, x.CreatedAt)).ToList();
        return new JobReport(r.Id, r.Number, r.Type.ToString(), r.Status.ToString(), r.WorkInstructionTitle, r.PartNumber, r.PartDescription, r.WorkOrder,
            r.QuantityTarget, r.QuantityCompleted, r.QuantityNotRepaired, people, people.Sum(p => p.Seconds), parts, parts.Sum(p => p.Quantity),
            files, files.Sum(x => x.Tested ?? 0), files.Sum(x => x.Passed ?? 0), files.Sum(x => x.Failed ?? 0), now);
    }

    public static string Hours(long seconds) => (seconds / 3600m).ToString("0.00", CultureInfo.InvariantCulture);
    static string N(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The chargeback job report as a PDF. Supplier, reference and rate are typed in when printing and are not stored.</summary>
    public async Task<(byte[] Pdf, string Name)> PdfAsync(Guid id, string? supplier, string? reference, decimal? rate, SequflohPrintService print, CancellationToken ct)
    {
        var x = await ReportAsync(id, ct);
        var rec = await tracking.LoadAsync(id, false, ct);
        var (printed, local) = await print.ClockAsync(rec.SiteId, ct);
        var org = await print.OrganizationAsync(ct);
        string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "____" : s.Trim();
        var hours = x.TotalSeconds / 3600m;
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Chargeback"),
            new PrintFields([("Supplier", Blank(supplier)), ("Our company", org), ("Supplier reference / PO", Blank(reference)), ("Report date", local(x.GeneratedAt))]),
            new PrintHeading("Job"),
            new PrintFields([("Work order", x.Number), ("Job type", x.Type), ("Work instruction", x.Title), ("Assembly part number", x.PartNumber ?? "—"), ("Description", x.PartDescription ?? "—"),
                ("Quantity target", x.QuantityTarget?.ToString(CultureInfo.InvariantCulture) ?? "—"), ("Quantity repaired", x.QuantityCompleted.ToString(CultureInfo.InvariantCulture)),
                ("Could not repair", x.QuantityNotRepaired.ToString(CultureInfo.InvariantCulture)), ("Status", x.Status)]),
            new PrintHeading("Man hours"),
            x.People.Count == 0 ? new PrintParagraph("No time has been logged on this work order.")
                : new PrintTable([("Technician", 3), ("Hours", 1.2)], [.. x.People.Select(p => new string?[] { p.Name, Hours(p.Seconds) }), ["Total", Hours(x.TotalSeconds)]]),
            new PrintHeading("Parts replaced"),
            x.Parts.Count == 0 ? new PrintParagraph("No parts replaced are listed on this work order.")
                : new PrintTable([("Part number", 2.5), ("Description", 3), ("Per assembly", 1.2), ("Assemblies", 1.2), ("Quantity replaced", 1.5)],
                    [.. x.Parts.Select(p => new string?[] { p.PartNumber, p.Description ?? "", N(p.PerAssembly), x.QuantityCompleted.ToString(CultureInfo.InvariantCulture), N(p.Quantity) + (p.Overridden ? " (entered)" : "") }),
                     ["Total", "", "", "", N(x.PartsTotal)]]),
            new PrintHeading("Cost"),
            new PrintFields([("Total man hours", Hours(x.TotalSeconds)), ("Rate", rate is { } rt ? rt.ToString("0.00", CultureInfo.InvariantCulture) : "____"),
                ("Labor cost", rate is { } r2 ? Math.Round(hours * r2, 2).ToString("0.00", CultureInfo.InvariantCulture) : "____ (computed when a rate is entered)")]),
            new PrintHeading("Test results summary"),
        };
        if (x.Files.Count == 0) blocks.Add(new PrintParagraph("No test result files are attached."));
        else
        {
            blocks.Add(new PrintFields([("Tested", x.Tested.ToString(CultureInfo.InvariantCulture)), ("Pass", x.Passed.ToString(CultureInfo.InvariantCulture)), ("Fail", x.Failed.ToString(CultureInfo.InvariantCulture)),
                ("Pass rate", x.Tested == 0 ? "—" : (x.Passed * 100m / x.Tested).ToString("0.0", CultureInfo.InvariantCulture) + "%")]));
            blocks.Add(new PrintTable([("Batch / date", 2), ("Tested", 1), ("Pass", 1), ("Fail", 1), ("Key values", 2.5), ("Source file", 2.5)],
                x.Files.Select(fl => new string?[]
                {
                    string.Join(" / ", new[] { fl.Batch, fl.TestDate?.ToString("yyyy-MM-dd") }.Where(s => s is not null)) is { Length: > 0 } bd ? bd : "—",
                    fl.Tested?.ToString(CultureInfo.InvariantCulture) ?? "not read", fl.Passed?.ToString(CultureInfo.InvariantCulture) ?? "", fl.Failed?.ToString(CultureInfo.InvariantCulture) ?? "",
                    string.Join("; ", fl.KeyValues.Select(k => $"{k.Column} {N(k.Min)}–{N(k.Max)} (avg {N(k.Average)})")), fl.FileName,
                }).ToList()));
        }
        blocks.Add(new PrintHeading("Sign-off"));
        blocks.Add(new PrintFields([("Prepared by", "________________"), ("Date", "________"), ("Supplier acknowledgement", "________________")]));
        var header = new PrintHeader(org, $"Chargeback Report: {x.PartNumber ?? x.Title}", x.Number, null, x.Status, null, string.IsNullOrWhiteSpace(supplier) ? null : $"Supplier: {supplier.Trim()}",
            "Job report", current.UserName ?? "", printed, null);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("JobReportPrinted", SequflohTrackingService.EntityType, rec.Id, $"Printed the job report for {x.Number}", rec.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"{x.Number}-job-report.pdf");
    }
}
