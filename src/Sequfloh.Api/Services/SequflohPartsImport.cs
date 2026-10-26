using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Imports;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>The file as read. HeaderRow and RowNumbers are row numbers in the file; Top holds the first rows so the header row can be chosen.</summary>
public sealed record PartsImportTable(string FileName, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, List<string> Warnings,
    int HeaderRow = 1, List<int>? RowNumbers = null, List<PartsImportTopRow>? Top = null);
public sealed record PartsImportTopRow(int Row, List<string> Cells);
public sealed record PartsImportRequest(string FileName, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, Guid SiteId, bool UpdateExisting,
    List<int>? RowNumbers = null, Guid? DefaultTypeId = null, bool KeepRepeats = false, Dictionary<string, string>? Fixed = null);
/// <summary>Repeat is true for an added row whose part number is already used by another row or part but whose values differ.</summary>
public sealed record PartsImportRow(int Row, string Action, string? PartNumber, string? Name, List<string> Messages, bool Repeat = false);
public sealed record ImportLayoutInput(string Name, System.Text.Json.JsonElement Layout);
public sealed record ImportLayoutView(Guid Id, string Name, System.Text.Json.JsonElement Layout);
public sealed record PartsImportPlan(List<PartsImportRow> Rows, int Add, int Update, int Skip, int Errors);

/// <summary>
/// Bulk import into the Part Master from Excel, CSV or the tables of a Word document. Files are only read, never run (macros are ignored).
/// The user maps columns, previews what will be added, updated or skipped, then confirms; the import is saved in one transaction and audited.
/// Serial numbers are not imported.
/// </summary>
public sealed class SequflohPartsImport(AppDbContext db, ModuleSupport m, SequflohFoundationService f, ILegacyOfficeConverter legacy, Sequfloh.Drawings.DrawingPartMatcher drawingLinks)
{
    public static readonly string[] Accepted = [".xlsx", ".xlsm", ".xls", ".csv", ".docx", ".doc"];
    public const int MaxRows = 5000;
    public const string NoNumberColumn = "Map at least one number column (Part number, OR #, DWG # or a cross-reference number such as NAPA) so each part gets a part number.";

    /// <summary>Importable fields and the header words that suggest them.</summary>
    public static readonly (string Key, string Label, string[] Words)[] Fields =
    [
        ("partNumber", "Part number", ["part number", "part no", "part #", "partnumber", "pn", "item", "item number", "item no", "sku", "part"]),
        ("name", "Name", ["name", "part name", "item name", "title"]),
        ("description", "Description", ["description", "desc", "details", "purchase description", "sales description"]),
        ("typeName", "Product family", ["product family", "family", "category", "item category", "product category", "part type", "type", "product type"]),
        ("partClass", "Part class", ["part class", "item type", "class"]),
        ("unitOfMeasure", "Unit of measure", ["unit of measure", "uom", "unit", "units"]),
        ("isAssembly", "Assembly", ["assembly", "is assembly", "assembly type"]),
        ("material", "Material", ["material"]),
        ("drawingNumber", "DWG #", ["sensen", "sensen #", "sensen#", "sensen no", "sensen number", "dwg #", "dwg#", "drawing number", "drawing", "drawing no", "dwg"]),
        ("specification", "Specification", ["specification", "spec"]),
        ("isRepairable", "Repairable", ["repairable", "can be repaired"]),
        ("isRepairPart", "Repair part", ["repair part", "repair parts", "spare part"]),
        ("isLotControlled", "Lot controlled", ["lot controlled", "lot control", "lot"]),
        .. PartAttributes.All.Select(a => ("attr:" + a.Key, a.Label, a.Words)),
    ];

    /// <summary>Which choice list a field takes ("families", "units", "yesNo", "lot"), or null for a text field.</summary>
    public static string? ChoiceKind(string key) => key switch
    {
        "name" or "typeName" or "partClass" => "families", "unitOfMeasure" => "units", "isAssembly" or "isRepairable" => "yesNo", "isLotControlled" => "lot", _ => null,
    };

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"[^a-z0-9# ]", "").Trim().Replace("  ", " ");

    public static Dictionary<string, int> Suggest(IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, int>();
        var used = new HashSet<int>();
        // exact header words first, then headers that start with one, so "Description" is not taken as "Name"
        foreach (var pass in new[] { 0, 1 })
            foreach (var (key, _, words) in Fields)
            {
                // Name takes product family choices, so a free-text name column is not picked on its own
                if (map.ContainsKey(key) || key == "name") continue;
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
    public async Task<PartsImportTable> ReadAsync(Stream input, string fileName, bool allTables, CancellationToken ct, int? headerRow = null)
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
        // keep the file's row numbers so problems can be reported against the sheet
        var numbered = grid.Select((r, i) => (Row: i + 1, Cells: r)).Where(x => x.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (numbered.Count == 0) throw new AppException("No rows were found in the file.");
        int headerAt;
        if (headerRow is { } chosen)
        {
            headerAt = numbered.FindIndex(x => x.Row == chosen);
            if (headerAt < 0) throw new AppException($"Row {chosen} is empty or not in the file.");
        }
        else headerAt = HeaderRow(numbered.Select(x => x.Cells).ToList());
        var headers = numbered[headerAt].Cells.Select(h => h.Trim()).ToList();
        var body = numbered.Skip(headerAt + 1).ToList();
        var rows = body.Select(x => Pad(x.Cells, headers.Count)).ToList();
        if (rows.Count > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        if (headers.Any(h => Norm(h).Contains("serial"))) warnings.Add("Serial number columns are not imported.");
        var top = numbered.Take(10).Select(x => new PartsImportTopRow(x.Row, x.Cells.Take(12).Select(c => c.Trim()).ToList())).ToList();
        return new PartsImportTable(Path.GetFileName(fileName), headers, rows, Suggest(headers), warnings, numbered[headerAt].Row, body.Select(x => x.Row).ToList(), top);
    }

    static List<string> Pad(List<string> r, int n) { var x = r.Take(n).Select(c => c.Trim()).ToList(); while (x.Count < n) x.Add(""); return x; }

    /// <summary>Of the first ten rows, the one whose cells match the most import fields (a part number column counts double); a stray title row above the headings is skipped.</summary>
    static int HeaderRow(List<List<string>> grid)
    {
        int best = 0, score = 0;
        for (var i = 0; i < Math.Min(grid.Count, 10); i++)
        {
            var m = Suggest(grid[i]);
            var sc = m.Count + (m.ContainsKey("partNumber") ? 1 : 0);
            if (sc > score) { best = i; score = sc; }
        }
        return best;
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
        // stored results only: recalculating could replace values that came from another workbook
        var wb = WorkbookReader.Read(s, ext.TrimStart('.'), recalculate: false);
        var sheet = wb.Sheets.FirstOrDefault(x => x.Visibility == "visible" && x.Cells.Count > 0) ?? throw new AppException("The workbook has no sheet with data.");
        if (wb.Sheets.Count(x => x.Visibility == "visible" && x.Cells.Count > 0) > 1) warnings.Add($"Read the first sheet with data: {sheet.Name}.");
        var grid = new List<List<string>>();
        for (var r = sheet.FirstRow; r <= sheet.LastRow; r++)
        {
            var row = new List<string>();
            for (var c = sheet.FirstCol; c <= sheet.LastCol; c++) { var cell = sheet.At(r, c); row.Add(cell?.Display ?? cell?.Text ?? cell?.Cached ?? ""); }
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

    /// <summary>Key is the row's mapped values, used to find exact duplicates; Position tells parts with the same OR # apart.</summary>
    sealed record Parsed(int Row, string? PartNumber, string? Name, string? Revision, List<string> Errors, Action<Part> Apply, string Key = "", string? Position = null, List<string>? Numbers = null);

    /// <summary>Attribute fields that hold a number used to find the same part again; descriptive fields are left out.</summary>
    public static bool IsNumberKey(string key) => key is "partNumber" or "drawingNumber" || (key.StartsWith("attr:", StringComparison.Ordinal) && PartAttributes.Find(key[5..]) is { } d && IsNumber(d));

    static bool IsNumber(PartAttributeDef a) => a.Key is not ("keyApplication" or "position" or "boxSize");

    /// <summary>The real numbers of an existing part: its part number, drawing number and cross-reference numbers.</summary>
    static IEnumerable<string> NumbersOf(Part x) => new[] { x.PartNumber, x.DrawingNumber ?? "" }
        .Concat(PartAttributes.Read(x.Attributes).Where(kv => PartAttributes.Find(kv.Key) is { } d && IsNumber(d)).Select(kv => kv.Value))
        .Select(v => v.Trim()).Where(v => !PartFields.IsBlank(v)).Distinct(StringComparer.OrdinalIgnoreCase);

    static bool? Flag(string v) => Norm(v) switch
    {
        "" => null, "y" or "yes" or "true" or "1" or "x" => true, "n" or "no" or "false" or "0" => false, _ => throw new FormatException(),
    };


    async Task<List<Parsed>> ParseAsync(PartsImportRequest r, CancellationToken ct)
    {
        if (r.Fixed?.Keys.FirstOrDefault(k => ChoiceKind(k) is null) is { } bad) throw new AppException($"\"{bad}\" cannot be the same for all rows.");
        // the name is always chosen in the dialog, never read from the file
        if (r.Mapping.ContainsKey("name")) throw new AppException("Name cannot come from a file column. Choose the Name for all rows.");
        if (string.IsNullOrWhiteSpace(r.Fixed?.GetValueOrDefault("name"))) throw new AppException("Choose the Name for all rows before you preview or import.");
        // Part number may be removed or not imported: the part number then comes from the other number columns
        if (!r.Mapping.Any(kv => IsNumberKey(kv.Key) && kv.Value >= 0)) throw new AppException(NoNumberColumn);
        if (r.Rows.Count > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        var partTypes = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.IsActive).Select(v => new { v.Id, v.Code, v.Label }).ToListAsync(ct);
        var familyHidden = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.HiddenFields != null).ToDictionaryAsync(v => v.Id, v => v.HiddenFields, ct);
        if (r.RowNumbers is { } rn && rn.Count != r.Rows.Count) throw new AppException("The row numbers do not match the rows.");
        if (r.DefaultTypeId is { } dt && partTypes.All(x => x.Id != dt)) throw new AppException("The product family for all rows is not an active product family.");
        var list = new List<Parsed>();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            string Raw(string key) => r.Fixed?.GetValueOrDefault(key) is { Length: > 0 } fx ? fx.Trim() : r.Mapping.TryGetValue(key, out var c) && c >= 0 && c < row.Count ? (row[c] ?? "").Trim() : "";
            // in a number column a placeholder such as "/", "-" or "N/A" reads as an empty cell, so it is never a part number or a match key
            string Get(string key) => Raw(key) is var v && IsNumberKey(key) && PartFields.IsBlank(v) ? "" : v;
            var errors = new List<string>();
            string? Text(string key, string label, int max) { var v = Get(key); if (v.Length == 0) return null; if (v.Length > max) { errors.Add($"{label} is longer than {max} characters."); return null; } return v; }
            string? Verbatim(string key, string label, int max) { var v = Raw(key); if (v.Length == 0) return null; if (v.Length > max) { errors.Add($"{label} is longer than {max} characters."); return null; } return v; }
            bool? FlagOf(string key, string label) { try { return Flag(Get(key)); } catch (FormatException) { errors.Add($"{label} must be Yes or No."); return null; } }
            var desc = Text("description", "Description", 4000);
            var attrs = new Dictionary<string, string>();
            foreach (var a in PartAttributes.All)
                if ((a.Verbatim ? Verbatim("attr:" + a.Key, a.Label, a.MaxLength) : Text("attr:" + a.Key, a.Label, a.MaxLength)) is { } av) attrs[a.Key] = av;
            // every real number in the row
            var numbers = new[] { Get("partNumber"), Get("drawingNumber") }.Concat(PartAttributes.All.Where(IsNumber).Select(a => attrs.GetValueOrDefault(a.Key) ?? ""))
                .Where(v => !PartFields.IsBlank(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pn = Text("partNumber", "Part number", 60);
            // no Part number: the OR #, else the first real number of the other mapped number columns, in column order
            if (pn is null && r.Mapping.Where(kv => IsNumberKey(kv.Key)).OrderBy(kv => kv.Key == "attr:orNumber" ? 0 : 1).ThenBy(kv => kv.Value).Select(kv => Get(kv.Key)).FirstOrDefault(v => v.Length is > 0 and <= 60) is { } other) pn = other;
            string? Family(string key, string label)
            {
                var v = Get(key);
                if (v.Length == 0) return null;
                var hit = partTypes.FirstOrDefault(x => SequflohFoundationService.ProductFamilyMatches(x.Code, x.Label, v));
                if (hit is null) errors.Add($"{label} \"{v}\" is not in the product families list.");
                return hit?.Label;
            }
            var chosenName = Family("name", "Name");
            var pclass = Family("partClass", "Part class");

            string? rev = null; // parts have no revision; new parts keep the internal default
            string? uom = null;
            if (Get("unitOfMeasure") is { Length: > 0 } u && (uom = PartChoices.Unit(u)) is null) errors.Add($"Unit of measure \"{u}\" must be Each, Lot, Batch or Skid.");
            var asm = FlagOf("isAssembly", "Assembly");
            var mat = Text("material", "Material", 200); var dwg = Text("drawingNumber", "Drawing number", 100); var spec = Text("specification", "Specification", 200);
            var rep = FlagOf("isRepairable", "Repairable"); var rp = FlagOf("isRepairPart", "Repair part"); string? lot = null;
            if (Get("isLotControlled") is { Length: > 0 } lv && (lot = PartChoices.Lot(lv)) is null) errors.Add($"Lot controlled \"{lv}\" must be Yes, No or Not applicable.");
            Guid? typeId = r.DefaultTypeId;
            var tn = Get("typeName");
            if (tn.Length > 0)
            {
                typeId = partTypes.FirstOrDefault(x => SequflohFoundationService.ProductFamilyMatches(x.Code, x.Label, tn))?.Id;
                if (typeId is null) errors.Add($"Product family \"{tn}\" is not in the product families list.");
            }
            var name = chosenName;
            // mapped fields that are empty or hold a placeholder ("/", "-", "N/A") are marked Not applicable on the part; the value is still kept
            var fieldKeys = PartFields.All.Select(x => x.Key).Where(k => PartAttributes.Find(k) is not { Verbatim: true }).ToHashSet();
            var blanks = r.Mapping.Keys.Select(k => k.StartsWith("attr:", StringComparison.Ordinal) ? k[5..] : k).Where(fieldKeys.Contains)
                .Where(k => PartFields.IsBlank(Raw(fieldKeys.Contains(k) && PartAttributes.Find(k) is not null ? "attr:" + k : k))).ToList();
            var key = string.Join("\u001f", r.Mapping.Keys.Concat(r.Fixed?.Keys ?? Enumerable.Empty<string>()).Distinct().Order(StringComparer.Ordinal).Select(k => k + "=" + Get(k)));
            list.Add(new Parsed(r.RowNumbers?[i] ?? i + 1, pn, name, rev, errors, p =>
            {
                // only mapped, non-empty values are written, so an update never blanks existing data
                if (name is not null) p.Name = name;
                if (desc is not null) p.Description = desc;
                if (uom is not null) p.UnitOfMeasure = uom;
                if (pclass is not null) p.PartClass = pclass;
                if (asm is not null) p.IsAssembly = asm.Value;
                if (mat is not null) p.Material = mat;
                if (dwg is not null) p.DrawingNumber = dwg;
                if (spec is not null) p.Specification = spec;
                if (rep is not null) p.IsRepairable = rep.Value;
                if (rp is not null) p.IsRepairPart = rp.Value;
                if (lot is not null) { p.LotControl = lot; p.IsLotControlled = lot == "Yes"; }
                if (typeId is not null) { p.TypeId = typeId; p.ProductFamily = partTypes.First(x => x.Id == typeId).Label; }
                PartAttributes.Merge(p, attrs);
                var hidden = PartFields.Read(p.HiddenFields);
                if (p.HiddenFields is null && p.TypeId is { } ft) hidden.AddRange(PartFields.Read(familyHidden.GetValueOrDefault(ft)));
                p.HiddenFields = PartFields.Write(hidden.Concat(blanks));
            }, key, attrs.GetValueOrDefault("position"), numbers));
        }
        return list;
    }

    public async Task<PartsImportPlan> PreviewAsync(PartsImportRequest r, CancellationToken ct)
    {
        var (plan, _, _) = await PlanAsync(r, ct);
        return plan;
    }

    /// <summary>
    /// Rows without any number and exact repeats of an earlier row are skipped. A row that repeats a part number with other values (for example another
    /// Position) is skipped, or added as its own part when KeepRepeats is on. An existing part is matched by part number and Position, or by any number
    /// of the row (part number, drawing number or a cross-reference number) that only one row of the file and only one existing part have.
    /// A row that matches more than one part is an error. Placeholders such as "/" are never numbers.
    /// </summary>
    async Task<(PartsImportPlan Plan, List<Parsed> Parsed, Dictionary<int, Part> Targets)> PlanAsync(PartsImportRequest r, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.SequflohEditParts, r.SiteId, null, ct);
        await m.RequireAsync(Permissions.MasterDataEdit, r.SiteId, null, ct);
        await m.ValidateLocationAsync(r.SiteId, null, ct);
        var parsed = await ParseAsync(r, ct);
        var all = await db.Parts.ToListAsync(ct);
        var existing = all.Where(p => !PartFields.IsBlank(p.PartNumber))
            .GroupBy(p => p.PartNumber, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        // a number shared by several existing parts, or by several different rows of the file, is not used to match
        var byNumber = all.SelectMany(x => NumbersOf(x).Select(n => (n, x))).GroupBy(t => t.n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().x, StringComparer.OrdinalIgnoreCase);
        var inFile = parsed.DistinctBy(p => p.Key).SelectMany(p => p.Numbers ?? []).GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<Guid>();
        var targets = new Dictionary<int, Part>();
        var rows = new List<PartsImportRow>();
        static string Pos(Part x) => PartAttributes.Read(x.Attributes).GetValueOrDefault("position") ?? "";
        foreach (var p in parsed)
        {
            if (p.PartNumber is null) { rows.Add(new(p.Row, "Skip", null, p.Name, ["No part numbers in this row."])); continue; }
            if (p.Errors.Count > 0) { rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, p.Errors)); continue; }
            if (exact.TryGetValue(p.PartNumber + "\u001e" + p.Key, out var same)) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Exact duplicate of row {same}."])); continue; }
            exact[p.PartNumber + "\u001e" + p.Key] = p.Row;
            var repeatOf = firstOf.TryGetValue(p.PartNumber, out var fr) ? fr : (int?)null;
            firstOf.TryAdd(p.PartNumber, p.Row);
            // an existing part with this part number: the one with the same Position, or the only one when the file has not used the number yet
            var candidates = existing.GetValueOrDefault(p.PartNumber)?.Where(x => !claimed.Contains(x.Id)).ToList() ?? [];
            var match = candidates.FirstOrDefault(x => string.Equals(Pos(x), p.Position ?? "", StringComparison.OrdinalIgnoreCase))
                ?? (repeatOf is null && existing.GetValueOrDefault(p.PartNumber)?.Count == 1 ? candidates.FirstOrDefault() : null);
            // or the one existing part that has another number of this row
            var hits = (p.Numbers ?? []).Where(n => inFile.GetValueOrDefault(n) == 1 && byNumber.ContainsKey(n)).Select(n => byNumber[n])
                .Concat(match is null ? [] : [match]).DistinctBy(x => x.Id).ToList();
            if (hits.Count > 1) { rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, [$"Matches more than one part in the Part Master: {string.Join(", ", hits.Select(x => x.PartNumber))}."])); continue; }
            if (hits.Count == 1 && claimed.Contains(hits[0].Id)) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Matches the same part as an earlier row ({hits[0].PartNumber})."])); continue; }
            match = hits.FirstOrDefault();
            if (match is not null)
            {
                claimed.Add(match.Id);
                if (!r.UpdateExisting) rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, ["Already in the Part Master."]));
                else if (!await m.CanAsync(Permissions.MasterDataEdit, match.SiteId, match.DepartmentId, ct) || !await m.CanAsync(Permissions.SequflohEditParts, match.SiteId, match.DepartmentId, ct))
                    rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, ["You cannot edit this existing part."]));
                else { rows.Add(new(p.Row, "Update", p.PartNumber, p.Name, PartFields.IsBlank(match.PartNumber) ? [$"Part number \"{match.PartNumber}\" becomes {p.PartNumber}."] : [])); targets[p.Row] = match; }
                continue;
            }
            var used = repeatOf is not null || existing.ContainsKey(p.PartNumber);
            if (!used) { rows.Add(new(p.Row, "Add", p.PartNumber, p.Name, [])); continue; }
            var where = repeatOf is { } rr ? $"row {rr}" : "a part already in the Part Master";
            if (!r.KeepRepeats) rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Same Part number as {where} with different values. Turn on \"Keep rows that repeat a Part number but differ\" to add it as its own part."]));
            else rows.Add(new(p.Row, "Add", p.PartNumber, p.Name, [$"Same Part number as {where}; added as its own part."], Repeat: true));
        }
        return (new PartsImportPlan(rows, rows.Count(x => x.Action == "Add"), rows.Count(x => x.Action == "Update"), rows.Count(x => x.Action == "Skip"), rows.Count(x => x.Action == "Error")), parsed, targets);
    }

    public async Task<PartsImportPlan> ImportAsync(PartsImportRequest r, CancellationToken ct)
    {
        var (plan, parsed, targets) = await PlanAsync(r, ct);
        if (plan.Add + plan.Update == 0) throw new AppException("There is nothing to import.");
        var today = await m.TodayAsync(r.SiteId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var byRow = parsed.ToDictionary(p => p.Row);
        var saved = new List<Guid>();
        foreach (var row in plan.Rows)
        {
            var p = byRow[row.Row];
            if (row.Action == "Add")
            {
                var part = new Part { PartNumber = p.PartNumber!, Name = p.Name!, UnitOfMeasure = "Each", SiteId = r.SiteId, Status = MasterStatus.Active, PartType = PartType.Component };
                part.Revision = p.Revision ?? "A"; // on an update the revision is changed only through Revise
                p.Apply(part);
                db.Parts.Add(part);
                saved.Add(part.Id);
                db.PartRevisions.Add(new PartRevision { PartId = part.Id, Revision = part.Revision, Reason = "Initial release (imported)", EffectiveOn = today });
            }
            else if (row.Action == "Update")
            {
                var t = targets[row.Row];
                // a part left numbered "/" (or another placeholder) by an earlier import takes the row's part number; a real number is never changed
                if (PartFields.IsBlank(t.PartNumber) && p.PartNumber is not null) t.PartNumber = p.PartNumber;
                p.Apply(t); saved.Add(t.Id);
            }
        }
        var file = Path.GetFileName(r.FileName);
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = "Import", EntityType = "PartsImport", EntityId = Guid.NewGuid(), SiteId = r.SiteId,
            Description = $"Imported parts from {file}: {plan.Add} added, {plan.Update} updated, {plan.Skip} skipped, {plan.Errors} with errors ({(r.UpdateExisting ? "update existing" : "skip existing")}).",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await drawingLinks.LinkPartsAsync(saved, ct); // drawings already named with a new or changed number link to these parts
        return plan;
    }

    // ------------------------------------------------------------------ saved column layouts
    public async Task<List<ImportLayoutView>> LayoutsAsync(CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        var rows = await db.SequflohImportLayouts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        return rows.Select(View).ToList();
    }

    public async Task<ImportLayoutView> SaveLayoutAsync(ImportLayoutInput r, CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        var name = CheckLayout(r);
        var x = await db.SequflohImportLayouts.FirstOrDefaultAsync(l => l.Name.ToLower() == name.ToLower(), ct);
        var action = x is null ? "Create" : "Update";
        if (x is null) { x = new SequflohImportLayout { Name = name }; db.SequflohImportLayouts.Add(x); }
        x.Layout = r.Layout.GetRawText();
        LayoutAudit(action, x.Id, action == "Create" ? $"Saved import layout {name}." : $"Replaced import layout {name}.");
        await db.SaveChangesAsync(ct);
        return View(x);
    }

    public async Task<ImportLayoutView> RenameLayoutAsync(Guid id, string newName, CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        var x = await db.SequflohImportLayouts.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Import layout");
        ModuleSupport.Required(newName, "Name", 200); var name = newName.Trim();
        if (await db.SequflohImportLayouts.AnyAsync(l => l.Id != id && l.Name.ToLower() == name.ToLower(), ct)) throw new AppException($"A layout named {name} already exists.");
        LayoutAudit("Rename", x.Id, $"Renamed import layout {x.Name} to {name}.");
        x.Name = name;
        await db.SaveChangesAsync(ct);
        return View(x);
    }

    public async Task DeleteLayoutAsync(Guid id, CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        var x = await db.SequflohImportLayouts.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Import layout");
        db.SequflohImportLayouts.Remove(x);
        LayoutAudit("Delete", x.Id, $"Deleted import layout {x.Name}.");
        await db.SaveChangesAsync(ct);
    }

    static string CheckLayout(ImportLayoutInput r)
    {
        ModuleSupport.Required(r.Name, "Name", 200); var name = r.Name.Trim();
        if (r.Layout.ValueKind != System.Text.Json.JsonValueKind.Object) throw new AppException("The layout is missing.");
        if (r.Layout.GetRawText().Length > 200_000) throw new AppException("The layout is too large.");
        return name;
    }

    static ImportLayoutView View(SequflohImportLayout x) => new(x.Id, x.Name, System.Text.Json.JsonDocument.Parse(x.Layout).RootElement.Clone());

    void LayoutAudit(string action, Guid id, string description) => db.AuditEntries.Add(new AuditEntry
    {
        OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = action, EntityType = "ImportLayout", EntityId = id, Description = description,
    });
}
