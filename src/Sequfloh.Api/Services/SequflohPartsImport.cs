using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record PartsImportTable(string FileName, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, List<string> Warnings);
public sealed record PartsImportRequest(string FileName, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, Guid SiteId, bool UpdateExisting);
public sealed record PartsImportRow(int Row, string Action, string? PartNumber, string? Name, List<string> Messages);
public sealed record PartsImportPlan(List<PartsImportRow> Rows, int Add, int Update, int Skip, int Errors);

/// <summary>
/// Bulk import into the Part Master from Excel, CSV or the tables of a Word document. Files are only read, never run (macros are ignored).
/// The user maps columns, previews what will be added, updated or skipped, then confirms; the import is saved in one transaction and audited.
/// Serial numbers are not imported.
/// </summary>
public sealed class SequflohPartsImport(AppDbContext db, ModuleSupport m, SequflohFoundationService f, ILegacyOfficeConverter legacy)
{
    public static readonly string[] Accepted = [".xlsx", ".xlsm", ".xls", ".csv", ".docx", ".doc"];
    public const int MaxRows = 5000;

    /// <summary>Importable fields and the header words that suggest them.</summary>
    public static readonly (string Key, string Label, string[] Words)[] Fields =
    [
        ("partNumber", "Part number", ["part number", "part no", "part #", "partnumber", "pn", "item", "item number", "item no", "sku", "part"]),
        ("name", "Name", ["name", "part name", "item name", "title"]),
        ("description", "Description", ["description", "desc", "details", "purchase description", "sales description"]),
        ("revision", "Revision", ["revision", "rev", "rev level"]),
        ("partType", "Part type", ["part type", "type", "item type"]),
        ("unitOfMeasure", "Unit of measure", ["unit of measure", "uom", "unit", "units"]),
        ("productFamily", "Product family / category", ["product family", "family", "category", "class", "item category"]),
        ("assemblyType", "Assembly type", ["assembly type"]),
        ("material", "Material", ["material"]),
        ("drawingNumber", "Drawing number", ["drawing number", "drawing", "drawing no", "dwg"]),
        ("specification", "Specification", ["specification", "spec"]),
        ("isRepairable", "Repairable", ["repairable", "can be repaired"]),
        ("isRepairPart", "Repair part", ["repair part", "repair parts", "spare part"]),
        ("isLotControlled", "Lot controlled", ["lot controlled", "lot control", "lot"]),
        ("statusTag", "Official status in NetSuite", ["netsuite status", "official status", "status in netsuite", "netsuite"]),
    ];

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"[^a-z0-9# ]", "").Trim().Replace("  ", " ");

    public static Dictionary<string, int> Suggest(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>();
        var used = new HashSet<int>();
        // exact header words first, then headers that start with one, so "Description" is not taken as "Name"
        foreach (var pass in new[] { 0, 1 })
            foreach (var (key, _, words) in Fields)
            {
                if (map.ContainsKey(key)) continue;
                for (var i = 0; i < headers.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    var h = Norm(headers[i]);
                    if (h.Length == 0 || h.Contains("serial")) continue;
                    if (pass == 0 ? words.Contains(h) : words.Any(w => w.Length > 3 && h.StartsWith(w + " "))) { map[key] = i; used.Add(i); break; }
                }
            }
        return map;
    }

    // ------------------------------------------------------------------ reading
    public async Task<PartsImportTable> ReadAsync(Stream input, string fileName, bool allTables, CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Accepted.Contains(ext)) throw new AppException("Choose an Excel (.xlsx, .xlsm, .xls), CSV or Word (.docx, .doc) file.");
        var ms = new MemoryStream();
        await input.CopyToAsync(ms, ct);
        ms.Position = 0;
        (ext, var word) = await ImportedFileService.EffectiveFormatAsync(ms, ext, ct);
        ms.Position = 0;
        if (await ImportSecurity.InspectAsync(ms, ext, ct) is { } problem) throw new AppException(problem);
        ms.Position = 0;
        var warnings = new List<string>();
        if (ext is ".xls" or ".doc" or ".rtf" or ".html" or ".mht")
        {
            // older and web-page formats are read from a converted copy; the upload itself is not changed
            ms = new MemoryStream(await ImportedFileService.ConvertLegacyAsync(legacy, ms, ext, word, ct));
            ext = word ? ".docx" : ".xlsx";
        }
        List<List<string>> grid = ext switch
        {
            ".csv" => ReadCsv(ms),
            ".docx" => ReadDocx(ms, allTables, warnings),
            _ => ReadWorkbook(ms, ext, warnings),
        };
        if (ext is ".xlsm") warnings.Add("Macros in the workbook were ignored; only cell values were read.");
        grid = grid.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (grid.Count == 0) throw new AppException("No rows were found in the file.");
        var headerAt = HeaderRow(grid);
        var headers = grid[headerAt].Select(h => h.Trim()).ToList();
        var rows = grid.Skip(headerAt + 1).Select(r => Pad(r, headers.Count)).ToList();
        if (rows.Count > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        if (headers.Any(h => Norm(h).Contains("serial"))) warnings.Add("Serial number columns are not imported.");
        return new PartsImportTable(Path.GetFileName(fileName), headers, rows, Suggest(headers), warnings);
    }

    static List<string> Pad(List<string> r, int n) { var x = r.Take(n).Select(c => c.Trim()).ToList(); while (x.Count < n) x.Add(""); return x; }

    /// <summary>The first of the top rows that names a part number column, or the first non-empty row.</summary>
    static int HeaderRow(List<List<string>> grid)
    {
        for (var i = 0; i < Math.Min(grid.Count, 10); i++)
            if (Suggest(grid[i]).ContainsKey("partNumber")) return i;
        return 0;
    }

    static List<List<string>> ReadCsv(Stream s)
    {
        var a = CsvParser.Analyze(s, null);
        s.Position = 0;
        using var reader = new StreamReader(s, CsvParser.EncodingFor(a.Encoding), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return CsvParser.Read(reader, a.Delimiter).Select(r => r.ToList()).ToList();
    }

    static List<List<string>> ReadWorkbook(Stream s, string ext, List<string> warnings)
    {
        var wb = WorkbookReader.Read(s, ext.TrimStart('.'));
        var sheet = wb.Sheets.FirstOrDefault(x => x.Visibility == "visible" && x.Cells.Count > 0) ?? throw new AppException("The workbook has no sheet with data.");
        if (wb.Sheets.Count(x => x.Visibility == "visible" && x.Cells.Count > 0) > 1) warnings.Add($"Read the first sheet with data: {sheet.Name}.");
        var grid = new List<List<string>>();
        for (var r = sheet.FirstRow; r <= sheet.LastRow; r++)
        {
            var row = new List<string>();
            for (var c = sheet.FirstCol; c <= sheet.LastCol; c++) { var cell = sheet.At(r, c); row.Add(cell?.Text ?? cell?.Cached ?? ""); }
            grid.Add(row);
        }
        return grid;
    }

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Cell text of the document's tables (the first, or all of them with matching headers appended). Only document.xml is read.</summary>
    public static List<List<string>> ReadDocx(Stream s, bool allTables, List<string> warnings)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("word/document.xml") ?? throw new AppException("The Word document has no body.");
        XDocument doc;
        using (var es = entry.Open()) doc = XDocument.Load(es, LoadOptions.None);
        var tables = doc.Descendants(W + "tbl").Where(t => !t.Ancestors(W + "tbl").Any()).ToList();
        if (tables.Count == 0) throw new AppException("The Word document has no table. Put the parts in a table with a header row.");
        List<List<string>> Rows(XElement t) => t.Elements(W + "tr").Select(tr => tr.Elements(W + "tc")
            .Select(tc => string.Join(" ", tc.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(x => x.Value))).Where(x => x.Length > 0))).ToList()).ToList();
        var grid = Rows(tables[0]);
        if (!allTables) { if (tables.Count > 1) warnings.Add($"Read the first of {tables.Count} tables."); return grid; }
        var header = grid.FirstOrDefault()?.Select(Norm).ToList() ?? [];
        foreach (var t in tables.Skip(1))
        {
            var rows = Rows(t);
            if (rows.Count == 0) continue;
            if (rows[0].Select(Norm).SequenceEqual(header)) grid.AddRange(rows.Skip(1));
            else warnings.Add("A table with different column headings was left out.");
        }
        return grid;
    }

    // ------------------------------------------------------------------ preview and import
    async Task RequireCanImportAsync(CancellationToken ct)
    {
        if (!await m.CanAnyAsync(Permissions.SequflohEditParts, ct) || !await m.CanAnyAsync(Permissions.MasterDataEdit, ct)) throw new ForbiddenException();
    }

    sealed record Parsed(int Row, string? PartNumber, string? Name, string? Revision, List<string> Errors, Action<Part> Apply);

    static bool? Flag(string v) => Norm(v) switch
    {
        "" => null, "y" or "yes" or "true" or "1" or "x" => true, "n" or "no" or "false" or "0" => false, _ => throw new FormatException(),
    };

    async Task<List<Parsed>> ParseAsync(PartsImportRequest r, CancellationToken ct)
    {
        if (!r.Mapping.TryGetValue("partNumber", out _)) throw new AppException("Choose the column that holds the part number.");
        if (!r.Mapping.ContainsKey("name") && !r.Mapping.ContainsKey("description")) throw new AppException("Choose a column for the name or the description.");
        if (r.Rows.Count > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        var tags = await f.OptionsAsync("status-tags", ct);
        var list = new List<Parsed>();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            string Get(string key) => r.Mapping.TryGetValue(key, out var c) && c >= 0 && c < row.Count ? (row[c] ?? "").Trim() : "";
            var errors = new List<string>();
            string? Text(string key, string label, int max) { var v = Get(key); if (v.Length == 0) return null; if (v.Length > max) { errors.Add($"{label} is longer than {max} characters."); return null; } return v; }
            bool? FlagOf(string key, string label) { try { return Flag(Get(key)); } catch (FormatException) { errors.Add($"{label} must be Yes or No."); return null; } }
            var pn = Text("partNumber", "Part number", 60);
            var desc = Text("description", "Description", 4000);
            var name = Text("name", "Name", 200) ?? (desc is null ? null : desc.Length > 200 ? desc[..200] : desc);
            if (pn is null) errors.Add("Part number is empty.");
            if (name is null) errors.Add("Name or description is empty.");
            var rev = Text("revision", "Revision", 10);
            var uom = Text("unitOfMeasure", "Unit of measure", 20);
            PartType? type = null;
            var t = Get("partType");
            if (t.Length > 0) { if (Enum.TryParse<PartType>(t.Replace(" ", ""), true, out var pt) && Enum.IsDefined(pt)) type = pt; else errors.Add($"Part type \"{t}\" is not known."); }
            var family = Text("productFamily", "Product family", 100); var asm = Text("assemblyType", "Assembly type", 100);
            var mat = Text("material", "Material", 200); var dwg = Text("drawingNumber", "Drawing number", 100); var spec = Text("specification", "Specification", 200);
            var rep = FlagOf("isRepairable", "Repairable"); var rp = FlagOf("isRepairPart", "Repair part"); var lot = FlagOf("isLotControlled", "Lot controlled");
            string? tag = null;
            var tg = Get("statusTag");
            if (tg.Length > 0) { tag = tags.FirstOrDefault(x => string.Equals(x, tg, StringComparison.OrdinalIgnoreCase)); if (tag is null) errors.Add($"\"{tg}\" is not an official status in the list."); }
            list.Add(new Parsed(i + 1, pn, name, rev, errors, p =>
            {
                // only mapped, non-empty values are written, so an update never blanks existing data
                if (name is not null && (r.Mapping.ContainsKey("name") || p.Name is null or "")) p.Name = name;
                if (desc is not null) p.Description = desc;
                if (uom is not null) p.UnitOfMeasure = uom;
                if (type is not null) p.PartType = type.Value;
                if (family is not null) p.ProductFamily = family;
                if (asm is not null) p.AssemblyType = asm;
                if (mat is not null) p.Material = mat;
                if (dwg is not null) p.DrawingNumber = dwg;
                if (spec is not null) p.Specification = spec;
                if (rep is not null) p.IsRepairable = rep.Value;
                if (rp is not null) p.IsRepairPart = rp.Value;
                if (lot is not null) p.IsLotControlled = lot.Value;
                if (tag is not null) p.StatusTag = tag;
            }));
        }
        return list;
    }

    public async Task<PartsImportPlan> PreviewAsync(PartsImportRequest r, CancellationToken ct)
    {
        var (plan, _, _) = await PlanAsync(r, ct);
        return plan;
    }

    async Task<(PartsImportPlan Plan, List<Parsed> Parsed, Dictionary<string, Part> Existing)> PlanAsync(PartsImportRequest r, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohEditParts, r.SiteId, null, ct);
        await m.RequireAsync(Permissions.MasterDataEdit, r.SiteId, null, ct);
        await m.ValidateLocationAsync(r.SiteId, null, ct);
        var parsed = await ParseAsync(r, ct);
        var numbers = parsed.Where(p => p.PartNumber is not null).Select(p => p.PartNumber!).Distinct().ToList();
        var existing = await db.Parts.Where(p => numbers.Contains(p.PartNumber)).ToDictionaryAsync(p => p.PartNumber, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PartsImportRow>();
        foreach (var p in parsed)
        {
            if (p.Errors.Count > 0) { rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, p.Errors)); continue; }
            if (!seen.Add(p.PartNumber!)) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, ["Duplicate part number in the file; the first row is used."])); continue; }
            if (existing.TryGetValue(p.PartNumber!, out var ex))
            {
                if (!r.UpdateExisting) rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, ["Already in the Part Master."]));
                else if (!await m.CanAsync(Permissions.MasterDataEdit, ex.SiteId, ex.DepartmentId, ct) || !await m.CanAsync(Permissions.SequflohEditParts, ex.SiteId, ex.DepartmentId, ct))
                    rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, ["You cannot edit this existing part."]));
                else rows.Add(new(p.Row, "Update", p.PartNumber, p.Name, []));
                continue;
            }
            rows.Add(new(p.Row, "Add", p.PartNumber, p.Name, []));
        }
        return (new PartsImportPlan(rows, rows.Count(x => x.Action == "Add"), rows.Count(x => x.Action == "Update"), rows.Count(x => x.Action == "Skip"), rows.Count(x => x.Action == "Error")), parsed, existing);
    }

    public async Task<PartsImportPlan> ImportAsync(PartsImportRequest r, CancellationToken ct)
    {
        var (plan, parsed, existing) = await PlanAsync(r, ct);
        if (plan.Add + plan.Update == 0) throw new AppException("There is nothing to import.");
        var today = await m.TodayAsync(r.SiteId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var byRow = parsed.ToDictionary(p => p.Row);
        foreach (var row in plan.Rows)
        {
            var p = byRow[row.Row];
            if (row.Action == "Add")
            {
                var part = new Part { PartNumber = p.PartNumber!, Name = p.Name!, UnitOfMeasure = "EA", SiteId = r.SiteId, Status = MasterStatus.Active, PartType = PartType.Component };
                part.Revision = p.Revision ?? "A"; // on an update the revision is changed only through Revise
                p.Apply(part);
                db.Parts.Add(part);
                db.PartRevisions.Add(new PartRevision { PartId = part.Id, Revision = part.Revision, Reason = "Initial release (imported)", EffectiveOn = today });
            }
            else if (row.Action == "Update") p.Apply(existing[p.PartNumber!]);
        }
        var file = Path.GetFileName(r.FileName);
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = "Import", EntityType = "PartsImport", EntityId = Guid.NewGuid(), SiteId = r.SiteId,
            Description = $"Imported parts from {file}: {plan.Add} added, {plan.Update} updated, {plan.Skip} skipped, {plan.Errors} with errors ({(r.UpdateExisting ? "update existing" : "skip existing")}).",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return plan;
    }
}
