using Sequfloh.Infrastructure.Services;
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
    int HeaderRow = 1, List<int>? RowNumbers = null, List<PartsImportTopRow>? Top = null, List<PartsImportTab>? Tabs = null, List<string>? SkippedTabs = null);
/// <summary>One tab of a workbook read with "Import every tab": the tab name is the product family. HeaderRow and RowNumbers are row numbers in the tab.</summary>
public sealed record PartsImportTab(string Name, Guid FamilyId, string Family, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, int HeaderRow, List<int> RowNumbers);
public sealed record PartsImportTabCount(string Tab, string Family, int Rows, int Add, int Update, int Skip, int Errors);
public sealed record PartsImportTopRow(int Row, List<string> Cells);
public sealed record PartsImportRequest(string FileName, List<string> Headers, List<List<string>> Rows, Dictionary<string, int> Mapping, Guid SiteId, bool UpdateExisting,
    List<int>? RowNumbers = null, Guid? DefaultTypeId = null, bool KeepRepeats = false, Dictionary<string, string>? Fixed = null, List<PartsImportTab>? Tabs = null, List<string>? SkippedTabs = null, Guid? CustomerId = null);
/// <summary>Repeat is true for an added row whose part number is already used by another row or part but whose values differ.</summary>
public sealed record PartsImportRow(int Row, string Action, string? PartNumber, string? Name, List<string> Messages, bool Repeat = false, string? Tab = null, string? Saved = null, string? Incoming = null);
public sealed record ImportLayoutInput(string Name, System.Text.Json.JsonElement Layout);
public sealed record ImportLayoutView(Guid Id, string Name, System.Text.Json.JsonElement Layout);
public sealed record PartsImportPlan(List<PartsImportRow> Rows, int Add, int Update, int Skip, int Errors, List<PartsImportTabCount>? Tabs = null, List<string>? SkippedTabs = null, int Held = 0, int Unchanged = 0, string? LinkFailure = null, List<Guid>? SavedPartIds = null);

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
        ("brand", "Brand", ["brand", "brand name"]),
        ("partClass", "Part class", ["part class", "item type", "class"]),
        ("unitOfMeasure", "Unit of measure", ["unit of measure", "uom", "unit", "units"]),
        ("isAssembly", "Assembly", ["assembly", "is assembly", "assembly type"]),
        ("material", "Material", ["material"]),
        ("drawingRevision", "Drawing revision", ["drawing revision", "drawing rev", "dwg rev", "dwg revision"]),
        ("drawingNumber", "DWG #", ["sensen", "sensen #", "sensen#", "sensen no", "sensen number", "dwg #", "dwg#", "drawing number", "drawing", "drawing no", "dwg"]),
        ("upc", "UPC / Barcode", ["upc", "upc / barcode", "upc code", "barcode", "bar code", "ean", "gtin", "upc-a", "ean-13"]),
        ("specification", "Specification", ["specification", "spec"]),
        ("isRepairable", "Repairable", ["repairable", "can be repaired"]),
        ("isRepairPart", "Repair part", ["repair part", "repair parts", "spare part"]),
        ("isLotControlled", "Lot controlled", ["lot controlled", "lot control", "lot"]),
        .. PartAttributes.All.Select(a => ("attr:" + a.Key, a.Label, a.Words)),
    ];

    /// <summary>Which choice list a field takes ("families", "units", "yesNo", "lot"), or null for a text field.</summary>
    public static string? ChoiceKind(string key) => key switch
    {
        "name" or "typeName" or "partClass" => "families", "brand" => "brands", "unitOfMeasure" => "units", "isAssembly" or "isRepairable" => "yesNo", "isLotControlled" => "lot", _ => null,
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
    public async Task<PartsImportTable> ReadAsync(Stream input, string fileName, bool allTables, CancellationToken ct, int? headerRow = null, bool allTabs = false)
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
        if (allTabs)
        {
            if (ext is not (".xlsx" or ".xlsm")) throw new AppException("Import every tab works with Excel workbooks (.xlsx, .xlsm or .xls).");
            if (ext is ".xlsm") warnings.Add("Macros in the workbook were ignored; only cell values were read.");
            return await ReadTabsAsync(ms, ext, Path.GetFileName(fileName), warnings, ct);
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

    /// <summary>
    /// Every visible tab with data, each read like a file of its own (its own header row and column mapping). The tab name is the product family, matched
    /// like a typed family; a tab that matches no active product family is skipped and named in SkippedTabs.
    /// </summary>
    async Task<PartsImportTable> ReadTabsAsync(Stream ms, string ext, string fileName, List<string> warnings, CancellationToken ct)
    {
        var wb = WorkbookReader.Read(ms, ext.TrimStart('.'), recalculate: false);
        var families = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.IsActive)
            .OrderBy(v => v.SortOrder).Select(v => new { v.Id, v.Code, v.Label }).ToListAsync(ct);
        var tabs = new List<PartsImportTab>(); var skipped = new List<string>();
        foreach (var sheet in wb.Sheets.Where(x => x.Visibility == "visible" && x.Cells.Count > 0))
        {
            var fam = families.FirstOrDefault(x => SequflohFoundationService.ProductFamilyMatches(x.Code, x.Label, sheet.Name));
            if (fam is null) { skipped.Add($"Tab \"{sheet.Name}\" was skipped: it does not match a product family."); continue; }
            var grid = new List<List<string>>();
            for (var r = sheet.FirstRow; r <= sheet.LastRow; r++)
            {
                var row = new List<string>();
                for (var c = sheet.FirstCol; c <= sheet.LastCol; c++) { var cell = sheet.At(r, c); row.Add(cell?.Display ?? cell?.Text ?? cell?.Cached ?? ""); }
                grid.Add(row);
            }
            var numbered = grid.Select((r, i) => (Row: sheet.FirstRow + i, Cells: r)).Where(x => x.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
            if (numbered.Count < 2) { skipped.Add($"Tab \"{sheet.Name}\" was skipped: it has no rows under a header row."); continue; }
            var at = HeaderRow(numbered.Select(x => x.Cells).ToList());
            var headers = numbered[at].Cells.Select(h => h.Trim()).ToList();
            var body = numbered.Skip(at + 1).ToList();
            var map = Suggest(headers); map.Remove("typeName"); // the tab is the product family
            if (headers.Any(h => Norm(h).Contains("serial"))) warnings.Add($"Tab {sheet.Name}: serial number columns are not imported.");
            tabs.Add(new PartsImportTab(sheet.Name, fam.Id, fam.Label, headers, body.Select(x => Pad(x.Cells, headers.Count)).ToList(), map, numbered[at].Row, body.Select(x => x.Row).ToList()));
        }
        if (tabs.Count == 0) throw new AppException("No tab of the workbook matches a product family. Name each tab after its product family.");
        if (tabs.Sum(t => t.Rows.Count) > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        var first = tabs[0];
        return new PartsImportTable(fileName, first.Headers, first.Rows, first.Mapping, warnings, first.HeaderRow, first.RowNumbers, null, tabs, skipped);
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
    sealed record Parsed(int Row, string? PartNumber, string? Name, string? Revision, List<string> Errors, Action<Part> Apply, string Key = "", string? Position = null, List<string>? Numbers = null, string? Drawing = null, Dictionary<string, string>? Attrs = null, string? Upc = null, Guid? TypeId = null, string? Family = null, string? DrawingRevision = null);

    /// <summary>True when the part already has a product family, linked or only typed as text.</summary>
    static bool HasFamily(Part p) => p.TypeId is not null || !string.IsNullOrWhiteSpace(p.ProductFamily);

    /// <summary>The numbers an update would replace: an existing real DWG # or number field is never overwritten by a different value.</summary>
    static List<string> KeptNumbers(Part t, string? dwg, IReadOnlyDictionary<string, string>? attrs)
    {
        var kept = new List<string>();
        if (dwg is not null && !PartFields.IsBlank(t.DrawingNumber) && !string.Equals(t.DrawingNumber!.Trim(), dwg.Trim(), StringComparison.OrdinalIgnoreCase)) kept.Add($"Drawing number stays {t.DrawingNumber} (the file has {dwg}).");
        var have = PartAttributes.Read(t.Attributes);
        foreach (var (k, v) in attrs ?? new Dictionary<string, string>())
            if (PartAttributes.Find(k) is { } d && IsNumber(d) && have.GetValueOrDefault(k) is { } old && !PartFields.IsBlank(old) && !string.Equals(old.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase))
                kept.Add($"{d.Label} stays {old} (the file has {v}).");
        return kept;
    }

    /// <summary>Attribute fields that hold a number used to find the same part again; descriptive fields are left out.</summary>
    public static bool IsNumberKey(string key) => key is "partNumber" or "drawingNumber" || (key.StartsWith("attr:", StringComparison.Ordinal) && PartAttributes.Find(key[5..]) is { } d && IsMatchNumber(d));
    /// <summary>Number columns whose "/" and similar placeholders are read as blank, supplier numbers included.</summary>
    static bool IsPlaceholderKey(string key) => IsNumberKey(key) || (key.StartsWith("attr:", StringComparison.Ordinal) && PartAttributes.Find(key[5..]) is { } d && IsNumber(d));
    /// <summary>Numbers that find the same part again; supplier numbers such as the LINYI PN are not.</summary>
    static bool IsMatchNumber(PartAttributeDef a) => IsNumber(a) && a.MatchKey;

    static bool IsNumber(PartAttributeDef a) => a.Key is not ("keyApplication" or "position" or "boxSize");

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
        var brands = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.Brands && v.IsActive).Select(v => new { v.Id, v.Label }).ToListAsync(ct);
        var familyHidden = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.HiddenFields != null).ToDictionaryAsync(v => v.Id, v => v.HiddenFields, ct);
        if (r.RowNumbers is { } rn && rn.Count != r.Rows.Count) throw new AppException("The row numbers do not match the rows.");
        if (r.DefaultTypeId is { } dt && partTypes.All(x => x.Id != dt)) throw new AppException("The product family for all rows is not an active product family.");
        var list = new List<Parsed>();
        for (var i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i];
            string Raw(string key) => r.Fixed?.GetValueOrDefault(key) is { Length: > 0 } fx ? fx.Trim() : r.Mapping.TryGetValue(key, out var c) && c >= 0 && c < row.Count ? (row[c] ?? "").Trim() : "";
            // in a number column a placeholder such as "/", "-" or "N/A" reads as an empty cell, so it is never a part number or a match key
            string Get(string key) => Raw(key) is var v && (IsPlaceholderKey(key) || key == "drawingRevision") && PartFields.IsBlank(v) ? "" : v;
            var errors = new List<string>();
            string? Text(string key, string label, int max) { var v = Get(key); if (v.Length == 0) return null; if (v.Length > max) { errors.Add($"{label} is longer than {max} characters."); return null; } return v; }
            string? Verbatim(string key, string label, int max) { var v = Raw(key); if (v.Length == 0) return null; if (v.Length > max) { errors.Add($"{label} is longer than {max} characters."); return null; } return v; }
            bool? FlagOf(string key, string label) { try { return Flag(Get(key)); } catch (FormatException) { errors.Add($"{label} must be Yes or No."); return null; } }
            var desc = Text("description", "Description", 4000);
            var attrs = new Dictionary<string, string>();
            foreach (var a in PartAttributes.All)
                if ((a.Verbatim ? Verbatim("attr:" + a.Key, a.Label, a.MaxLength) : Text("attr:" + a.Key, a.Label, a.MaxLength)) is { } av) attrs[a.Key] = av;
            // every real number in the row
            var numbers = new[] { Get("partNumber"), Get("drawingNumber") }.Concat(PartAttributes.All.Where(IsMatchNumber).Select(a => attrs.GetValueOrDefault(a.Key) ?? ""))
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
            // the drawing revision is its own column. A trailing letter on the drawing number is left on the number (some drawings have no revision suffix).
            string? dwgRev = null;
            if (Get("drawingRevision") is { Length: > 0 } rawRev)
            {
                try { dwgRev = Sequfloh.Drawings.DrawingService.Revision(rawRev); }
                catch (AppException ex) { errors.Add(ex.Message); }
            }
            // UPC / Barcode: digits only, a UPC-E, UPC-A, EAN-13 or GTIN-14 length, with a right check digit
            var upc = Raw("upc") is { Length: > 0 } uv && !PartFields.IsBlank(uv) ? uv : null;
            if (upc is not null && (!Gtin.IsBarcode(upc) || !Gtin.CheckDigitOk(upc))) { errors.Add($"UPC / Barcode \"{upc}\" must be 8, 12, 13 or 14 digits with a right check digit."); upc = null; }
            var rep = FlagOf("isRepairable", "Repairable"); var rp = FlagOf("isRepairPart", "Repair part"); string? lot = null;
            if (Get("isLotControlled") is { Length: > 0 } lv && (lot = PartChoices.Lot(lv)) is null) errors.Add($"Lot controlled \"{lv}\" must be Yes, No or Not applicable.");
            Guid? brandId = null;
            var brandText = Get("brand");
            if (brandText.Length > 0)
            {
                brandId = brands.FirstOrDefault(x => string.Equals(x.Label.Trim(), brandText, StringComparison.OrdinalIgnoreCase))?.Id;
                if (brandId is null) errors.Add($"Brand \"{brandText}\" is not on the Brands list.");
            }
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
                // an existing real number is never replaced by a different one; blanks and placeholders are filled
                if (dwg is not null && PartFields.IsBlank(p.DrawingNumber)) p.DrawingNumber = Sequfloh.Drawings.DrawingRevisionRules.WholeNumber(dwg);
                if (dwgRev is not null && PartFields.IsBlank(p.DrawingRevision)) p.DrawingRevision = dwgRev;
                if (spec is not null) p.Specification = spec;
                // a UPC is only filled in where the part has none; an existing one is never replaced
                if (upc is not null && string.IsNullOrEmpty(p.Upc)) { p.Upc = upc; p.UpcKey = Gtin.Key(upc); }
                if (rep is not null) p.IsRepairable = rep.Value;
                if (rp is not null) p.IsRepairPart = rp.Value;
                if (lot is not null) { p.LotControl = lot; p.IsLotControlled = lot == "Yes"; }
                // a part that already has a product family keeps it; the file only sets the family of a part that has none
                if (typeId is not null && !HasFamily(p)) { p.TypeId = typeId; p.ProductFamily = partTypes.First(x => x.Id == typeId).Label; }
                if (brandId is not null) p.BrandId = brandId;
                var have = PartAttributes.Read(p.Attributes);
                PartAttributes.Merge(p, attrs.Where(kv => !(PartAttributes.Find(kv.Key) is { } d && IsNumber(d) && have.GetValueOrDefault(kv.Key) is { } old && !PartFields.IsBlank(old)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value));
                var hidden = PartFields.Read(p.HiddenFields);
                if (p.HiddenFields is null && p.TypeId is { } ft) hidden.AddRange(PartFields.Read(familyHidden.GetValueOrDefault(ft)));
                p.HiddenFields = PartFields.Write(hidden.Concat(blanks));
            }, key, attrs.GetValueOrDefault("position"), numbers, dwg, attrs, Upc: upc, TypeId: typeId, Family: typeId is { } fid ? partTypes.First(x => x.Id == fid).Label : null, DrawingRevision: dwgRev));
        }
        return list;
    }

    public const string ChooseCustomerAndLine = "Choose a customer and a product line. Parts are matched only inside that customer and product line, at the chosen site.";

    /// <summary>Test seam. When set, drawing links after a saved import are reported as failed and no links are written. Cleared by the test.</summary>
    internal static Exception? LinkFault { get; set; }

    sealed record ImportScope(Guid CustomerId, string Customer, Guid? LineId, string? Line);

    async Task<ImportScope> RequireScopeAsync(PartsImportRequest r, CancellationToken ct)
    {
        var columnIsTheLine = r.Mapping.ContainsKey("typeName") || r.Fixed?.ContainsKey("typeName") == true;
        if (r.CustomerId is null || (r.Tabs is null && r.DefaultTypeId is null && !columnIsTheLine)) throw new AppException(ChooseCustomerAndLine);
        var customer = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == r.CustomerId && v.ListKey == SequflohCatalog.Customers, ct)
            ?? throw new AppException("Choose a customer from the Customers list.");
        if (!customer.IsActive) throw new AppException($"{customer.Label} is deactivated. Choose an active customer.");
        if (r.Tabs is not null || r.DefaultTypeId is null) return new ImportScope(customer.Id, customer.Label, null, null);
        var line = await db.SequflohReferenceValues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == r.DefaultTypeId && v.ListKey == SequflohCatalog.ProductFamilies, ct)
            ?? throw new AppException("Choose a product line from the product families list.");
        if (!line.IsActive) throw new AppException($"{line.Label} is deactivated. Choose an active product line.");
        return new ImportScope(customer.Id, customer.Label, line.Id, line.Label);
    }

    public async Task<PartsImportPlan> PreviewAsync(PartsImportRequest r, CancellationToken ct)
    {
        var (plan, _, _) = await PlanAsync(r, ct);
        return Present(r, plan);
    }

    /// <summary>The held rows of a preview, as CSV, so they can be corrected and read again. Nothing is saved.</summary>
    public async Task<byte[]> HeldCsvAsync(PartsImportRequest r, CancellationToken ct)
    {
        var plan = await PreviewAsync(r, ct);
        var sb = new System.Text.StringBuilder();
        sb.Append("Row,Part number,Name,Saved,Incoming,Why\r\n");
        foreach (var row in plan.Rows.Where(x => x.Action == "Held"))
            sb.Append(string.Join(",", new[] { row.Tab is { } t ? $"{t} {row.Row}" : row.Row.ToString(), row.PartNumber, row.Name, row.Saved, row.Incoming, string.Join(" ", row.Messages) }.Select(Csv))).Append("\r\n");
        return System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>Runs the existing drawing matcher again for parts already saved. It only adds links.</summary>
    public async Task<(int Added, string? Failure)> RetryLinksAsync(List<Guid> partIds, CancellationToken ct)
    {
        await RequireCanImportAsync(ct);
        if (partIds.Count == 0) throw new AppException("Choose the parts whose drawing links should be tried again.");
        try
        {
            var added = await drawingLinks.LinkPartsAsync(partIds, ct);
            return (added, null);
        }
        catch (Exception ex) when (ex is not AppException and not ForbiddenException)
        {
            return (0, "Drawing links were not completed. The parts were not changed. Try the links again.");
        }
    }

    static string Csv(string? v)
    {
        var s = v ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>
    /// Rows without any number and exact repeats of an earlier row are skipped. A row that repeats a part number with other values (for example another
    /// Position) is skipped, or added as its own part when KeepRepeats is on. An existing part is matched by part number and Position inside the chosen
    /// customer, product line and site. A drawing number or another customer's number never selects a part. More than one possible part is held.
    /// A row whose drawing number or drawing revision disagrees with the part already saved is held, and that part is left unchanged.
    /// Placeholders such as "/" are never numbers.
    /// </summary>
    async Task<(PartsImportPlan Plan, List<Parsed> Parsed, Dictionary<int, Part> Targets)> PlanAsync(PartsImportRequest r, CancellationToken ct)
    {
        // parts are added at the chosen site, so both part permissions are needed there (a role for that site or for all sites); say which are missing
        var missing = new List<string>();
        foreach (var perm in new[] { Permissions.SequflohEditParts, Permissions.MasterDataEdit })
            if (!await m.CanAsync(perm, r.SiteId, null, ct)) missing.Add($"\"{Sequfloh.Platform.PermissionText.Label(perm)}\" ({perm})");
        if (missing.Count > 0)
        {
            var code = await db.Sites.AsNoTracking().Where(s => s.Id == r.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefaultAsync(ct) ?? "the chosen site";
            throw new ForbiddenException($"You cannot import parts at {code}: your roles do not give {string.Join(" and ", missing)} for this site. Choose another site, or ask an administrator to give you the role for {code} or for All sites.");
        }
        await m.ValidateLocationAsync(r.SiteId, null, ct);
        var scope = await RequireScopeAsync(r, ct);
        var parsed = r.Tabs is { } tabList ? await ParseTabsAsync(r, tabList, ct) : await ParseAsync(r, ct);
        string At(int row) => Label(r, row);
        var all = await db.Parts.ToListAsync(ct);
        // only parts of this customer, product line and site. A drawing number or another customer's alias never selects a part.
        bool InScope(Part p, Parsed row)
        {
            if (p.SiteId != r.SiteId || p.CustomerId != scope.CustomerId) return false;
            var line = scope.LineId ?? row.TypeId;
            return line is null || p.TypeId == line;
        }
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
            if (scope.LineId is { } selected && p.TypeId is { } got && got != selected)
            {
                rows.Add(new(p.Row, "Held", p.PartNumber, p.Name, [$"This row is for {p.Family ?? "another product line"}, and the import is for {scope.Line}. The row was not imported."], Saved: scope.Line, Incoming: p.Family));
                continue;
            }
            if (exact.TryGetValue(p.PartNumber + "\u001e" + p.Key, out var same)) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Exact duplicate of {At(same)}."])); continue; }
            exact[p.PartNumber + "\u001e" + p.Key] = p.Row;
            var repeatOf = firstOf.TryGetValue(p.PartNumber, out var fr) ? fr : (int?)null;
            firstOf.TryAdd(p.PartNumber, p.Row);
            // the customer part number, inside this customer, product line and site. Position separates allowed repeats. A shared drawing is not a match.
            var candidates = all.Where(x => InScope(x, p) && !claimed.Contains(x.Id) && string.Equals(x.PartNumber.Trim(), p.PartNumber.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var samePos = candidates.Where(x => string.Equals(Pos(x).Trim(), (p.Position ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (samePos.Count > 1)
            {
                rows.Add(new(p.Row, "Held", p.PartNumber, p.Name, [$"More than one {scope.Customer} part has this part number and position ({string.Join(", ", samePos.Select(x => x.PartNumber))}). The row was not imported."]));
                continue;
            }
            var match = samePos.Count == 1 ? samePos[0] : candidates.Count == 1 && repeatOf is null ? candidates[0] : null;
            if (candidates.Count > 1 && match is null)
            {
                if (!r.KeepRepeats) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Same Part number as a part already in {scope.Customer} with a different position. Turn on \"Keep rows that repeat a Part number but differ\" to add it as its own part."])); continue; }
            }
            if (match is not null)
            {
                claimed.Add(match.Id);
                if (!r.UpdateExisting) { rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, ["Already in the Part Master."])); continue; }
                if (!await m.CanAsync(Permissions.MasterDataEdit, match.SiteId, match.DepartmentId, ct) || !await m.CanAsync(Permissions.SequflohEditParts, match.SiteId, match.DepartmentId, ct))
                { rows.Add(new(p.Row, "Error", p.PartNumber, p.Name, ["You cannot edit this existing part."])); continue; }
                var savedDrawing = (match.DrawingNumber ?? "").Trim();
                var incomingDrawing = (p.Drawing ?? "").Trim();
                if (savedDrawing.Length > 0 && incomingDrawing.Length > 0 && !string.Equals(savedDrawing, incomingDrawing, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new(p.Row, "Held", p.PartNumber, p.Name, [$"Held {p.PartNumber}: saved drawing {savedDrawing}; file says {incomingDrawing}. Existing part and links unchanged."], Saved: savedDrawing, Incoming: incomingDrawing));
                    continue;
                }
                if (Sequfloh.Drawings.DrawingRevisionRules.Conflict(match.DrawingRevision, p.DrawingRevision) is { } revConflict)
                {
                    rows.Add(new(p.Row, "Held", p.PartNumber, p.Name, [revConflict + " Existing part and links unchanged."], Saved: match.DrawingRevision, Incoming: p.DrawingRevision));
                    continue;
                }
                var msgs = new List<string>();
                msgs.AddRange(KeptNumbers(match, null, p.Attrs));
                if (!PartFields.IsBlank(p.DrawingRevision) && PartFields.IsBlank(match.DrawingRevision)) msgs.Add($"Drawing revision set to {p.DrawingRevision}.");
                if (!WouldChange(match, p)) { rows.Add(new(p.Row, "Unchanged", p.PartNumber, p.Name, ["Already matches the file. Nothing was changed."])); continue; }
                rows.Add(new(p.Row, "Update", p.PartNumber, p.Name, msgs)); targets[p.Row] = match;
                continue;
            }
            var used = repeatOf is not null || candidates.Count > 0;
            if (!used) { rows.Add(new(p.Row, "Add", p.PartNumber, p.Name, [])); continue; }
            var where = repeatOf is { } rr ? At(rr) : "a part already in the Part Master";
            if (!r.KeepRepeats) rows.Add(new(p.Row, "Skip", p.PartNumber, p.Name, [$"Same Part number as {where} with different values. Turn on \"Keep rows that repeat a Part number but differ\" to add it as its own part."]));
            else rows.Add(new(p.Row, "Add", p.PartNumber, p.Name, [$"Same Part number as {where}; added as its own part."], Repeat: true));
        }
        // a UPC / Barcode can be on one part only: not one already on another part, and not twice in the file
        var byUpc = all.Where(x => x.UpcKey != null).ToDictionary(x => x.UpcKey!);
        var upcRow = new Dictionary<string, int>();
        var parsedByRow = parsed.GroupBy(x => x.Row).ToDictionary(g => g.Key, g => g.First());
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Action is not ("Add" or "Update") || parsedByRow.GetValueOrDefault(row.Row)?.Upc is not { } u) continue;
            var k = Gtin.Key(u)!;
            var target = targets.GetValueOrDefault(row.Row);
            if (target is not null && !string.IsNullOrEmpty(target.Upc))
            {
                if (target.UpcKey != k)
                {
                    rows[i] = row with { Action = "Held", Messages = [$"Held {row.PartNumber}: saved UPC / Barcode {target.Upc}; file says {u}. Existing part and links unchanged."], Saved = target.Upc, Incoming = u };
                    targets.Remove(row.Row);
                }
                continue;
            }
            if (byUpc.TryGetValue(k, out var owner) && owner.Id != target?.Id) { rows[i] = row with { Action = "Held", Messages = [$"Held {row.PartNumber}: UPC / Barcode {u} is already on part {owner.PartNumber}. The row was not imported."] }; targets.Remove(row.Row); continue; }
            if (upcRow.TryGetValue(k, out var first)) { rows[i] = row with { Action = "Held", Messages = [$"Held {row.PartNumber}: UPC / Barcode {u} is also on {At(first)}. A barcode can be on one part only. The row was not imported."] }; targets.Remove(row.Row); continue; }
            upcRow[k] = row.Row;
        }
        // a supplier LINYI PN is not a match key; one already on another part or repeated in the file is pointed out for a check, not refused
        var byLinyi = all.Select(x => (Part: x, V: PartAttributes.Read(x.Attributes).GetValueOrDefault("linyiPn")?.Trim())).Where(x => !PartFields.IsBlank(x.V))
            .GroupBy(x => x.V!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.Part).ToList(), StringComparer.OrdinalIgnoreCase);
        var linyiRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Action is not ("Add" or "Update") || parsedByRow.GetValueOrDefault(row.Row)?.Attrs?.GetValueOrDefault("linyiPn")?.Trim() is not { Length: > 0 } v) continue;
            var target = targets.GetValueOrDefault(row.Row);
            if (target is not null && !PartFields.IsBlank(PartAttributes.Read(target.Attributes).GetValueOrDefault("linyiPn"))) continue;
            if (byLinyi.GetValueOrDefault(v)?.Where(x => x.Id != target?.Id).Select(x => x.PartNumber).ToList() is { Count: > 0 } owners) rows[i] = row = row with { Messages = [.. row.Messages, $"LINYI PN {v} is already on part {string.Join(", ", owners)}. Check this is right."] };
            if (linyiRow.TryGetValue(v, out var first)) rows[i] = row with { Messages = [.. row.Messages, $"LINYI PN {v} is also on {At(first)}. Check this is right."] };
            else linyiRow[v] = row.Row;
        }
        return (new PartsImportPlan(rows, rows.Count(x => x.Action == "Add"), rows.Count(x => x.Action == "Update"), rows.Count(x => x.Action == "Skip"), rows.Count(x => x.Action == "Error"), Held: rows.Count(x => x.Action == "Held"), Unchanged: rows.Count(x => x.Action == "Unchanged")), parsed, targets);
    }

    /// <summary>True when applying the row would change a stored field. Blank incoming values do not count.</summary>
    static bool WouldChange(Part p, Parsed row)
    {
        var copy = new Part
        {
            Name = p.Name, Description = p.Description, UnitOfMeasure = p.UnitOfMeasure, PartClass = p.PartClass, IsAssembly = p.IsAssembly,
            Material = p.Material, DrawingNumber = p.DrawingNumber, DrawingRevision = p.DrawingRevision, Specification = p.Specification,
            Upc = p.Upc, UpcKey = p.UpcKey, IsRepairable = p.IsRepairable, IsRepairPart = p.IsRepairPart, LotControl = p.LotControl,
            IsLotControlled = p.IsLotControlled, TypeId = p.TypeId, ProductFamily = p.ProductFamily, Attributes = p.Attributes,
            HiddenFields = p.HiddenFields, PartNumber = p.PartNumber, CustomerId = p.CustomerId, BrandId = p.BrandId,
        };
        row.Apply(copy);
        return copy.Name != p.Name || copy.Description != p.Description || copy.UnitOfMeasure != p.UnitOfMeasure || copy.PartClass != p.PartClass
            || copy.IsAssembly != p.IsAssembly || copy.Material != p.Material || copy.DrawingNumber != p.DrawingNumber || copy.DrawingRevision != p.DrawingRevision
            || copy.Specification != p.Specification || copy.Upc != p.Upc || copy.IsRepairable != p.IsRepairable || copy.IsRepairPart != p.IsRepairPart
            || copy.LotControl != p.LotControl || copy.TypeId != p.TypeId || copy.BrandId != p.BrandId || !SameAttrs(copy.Attributes, p.Attributes);
    }

    static bool SameAttrs(string? a, string? b)
    {
        var left = PartAttributes.Read(a);
        var right = PartAttributes.Read(b);
        return left.Count == right.Count && left.All(kv => right.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    public async Task<PartsImportPlan> ImportAsync(PartsImportRequest r, CancellationToken ct)
    {
        var scope = await RequireScopeAsync(r, ct);
        var today = await m.TodayAsync(r.SiteId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"{r.SiteId:N}|{r.CustomerId:N}|{r.DefaultTypeId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var (plan, parsed, targets) = await PlanAsync(r, ct);
        if (plan.Add + plan.Update == 0 && plan.Unchanged == 0) throw new AppException(plan.Held > 0 ? "Nothing was imported. Every row was skipped or held for review." : "There is nothing to import.");
        var byRow = parsed.ToDictionary(p => p.Row);
        var saved = new List<Guid>();
        foreach (var row in plan.Rows)
        {
            var p = byRow[row.Row];
            if (row.Action == "Add")
            {
                var part = new Part { PartNumber = p.PartNumber!, Name = p.Name!, UnitOfMeasure = "Each", SiteId = r.SiteId, Status = MasterStatus.Active, PartType = PartType.Component, CustomerId = r.CustomerId };
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
                var before = t.PartNumber;
                if (PartFields.IsBlank(t.PartNumber) && p.PartNumber is not null) t.PartNumber = p.PartNumber;
                if (r.Tabs is null) p.Apply(t); else FillBlanks(p.Apply, t); // every-tab imports only fill in what the part does not have yet
                saved.Add(t.Id);
                db.AuditEntries.Add(new AuditEntry
                {
                    OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = "Import", EntityType = "Part", EntityId = t.Id, SiteId = t.SiteId,
                    Description = $"Updated part {t.PartNumber} from {Path.GetFileName(r.FileName)} ({(r.Tabs is null ? $"row {row.Row}" : Label(r, row.Row))})." + (before != t.PartNumber ? $" Part number changed from \"{before}\" to {t.PartNumber}." : "")
                        + (row.Messages.Where(x => x.Contains(" stays ") || x.StartsWith("Family kept", StringComparison.Ordinal)) is var k && k.Any() ? " " + string.Join(" ", k) : ""),
                });
            }
        }
        var file = Path.GetFileName(r.FileName);
        var heldNotes = plan.Rows.Where(x => x.Action == "Held").Take(20).Select(x => $"{x.PartNumber}: {string.Join(" ", x.Messages)}").ToList();
        var families = plan.Rows.Where(x => x.Action is "Add" or "Update" or "Unchanged").Select(x => byRow.GetValueOrDefault(x.Row)?.Family).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        var line = scope.Line ?? (r.Tabs is { } tabs ? string.Join(", ", tabs.Select(t => t.Family).Distinct()) : families.Count > 0 ? string.Join(", ", families) : "the selected product lines");
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = m.Now, UserId = m.UserId, UserName = m.Current.UserName, Action = "Import", EntityType = "PartsImport", EntityId = Guid.NewGuid(), SiteId = r.SiteId,
            Description = $"Imported {scope.Customer} / {line} from {file}: {plan.Add} added, {plan.Update} updated, {plan.Unchanged} unchanged, {plan.Held} held, {plan.Skip} skipped, {plan.Errors} with errors. Existing part IDs and links were kept."
                + (heldNotes.Count > 0 ? " Held: " + string.Join(" ", heldNotes) : "")
                + (r.Tabs is { } tl ? $" Tabs: {string.Join(", ", tl.Select(t => $"{t.Name} ({t.Family})"))}." + (r.SkippedTabs is { Count: > 0 } st ? $" Skipped: {string.Join(" ", st)}" : "") : ""),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        string? linkFailure = null;
        try
        {
            if (LinkFault is { } fault) { LinkFault = null; throw fault; }
            await drawingLinks.LinkPartsAsync(saved, ct); // drawings already named with a number link to these parts; links are only added
        }
        catch (Exception ex) when (ex is not AppException and not ForbiddenException)
        {
            linkFailure = "Drawing links were not completed. The parts were saved and were not created again. Try the links again.";
        }
        return Present(r, plan with { LinkFailure = linkFailure, SavedPartIds = saved });
    }

    // ------------------------------------------------------------------ every tab of a workbook
    /// <summary>Rows of an every-tab import are numbered tab * TabRows + row inside the plan, so rows of different tabs never collide.</summary>
    const int TabRows = 1_000_000;

    static string Label(PartsImportRequest r, int row) =>
        r.Tabs is { } t && row >= TabRows && row / TabRows - 1 < t.Count ? $"Tab {t[row / TabRows - 1].Name}, row {row % TabRows}" : $"row {row}";

    async Task<List<Parsed>> ParseTabsAsync(PartsImportRequest r, List<PartsImportTab> tabs, CancellationToken ct)
    {
        if (tabs.Count == 0) throw new AppException("No tab to import.");
        if (tabs.Sum(t => t.Rows.Count) > MaxRows) throw new AppException($"A file can import at most {MaxRows} parts at a time.");
        var active = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies && v.IsActive).Select(v => v.Id).ToListAsync(ct);
        var list = new List<Parsed>();
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            if (!active.Contains(t.FamilyId)) throw new AppException($"Tab {t.Name}: {t.Family} is not an active product family.");
            if (t.RowNumbers.Count != t.Rows.Count) throw new AppException($"Tab {t.Name}: the row numbers do not match the rows.");
            var map = new Dictionary<string, int>(t.Mapping); map.Remove("typeName");
            var fx = new Dictionary<string, string>(r.Fixed ?? []); fx.Remove("typeName");
            if (string.IsNullOrWhiteSpace(fx.GetValueOrDefault("name"))) fx["name"] = t.Family; // the Name is the tab's product family unless one is chosen
            try
            {
                list.AddRange(await ParseAsync(r with { Headers = t.Headers, Rows = t.Rows, Mapping = map, Fixed = fx, DefaultTypeId = t.FamilyId, Tabs = null,
                    RowNumbers = t.RowNumbers.Select(n => (i + 1) * TabRows + n).ToList() }, ct));
            }
            catch (AppException e) when (e is not ForbiddenException) { throw new AppException($"Tab {t.Name}: {e.Message}"); }
        }
        return list;
    }

    /// <summary>Plan rows with their tab and the row number inside the tab, and the counts of each tab.</summary>
    static PartsImportPlan Present(PartsImportRequest r, PartsImportPlan plan)
    {
        if (r.Tabs is not { } tabs) return plan;
        var rows = plan.Rows.Select(x => x.Row >= TabRows ? x with { Row = x.Row % TabRows, Tab = tabs[x.Row / TabRows - 1].Name } : x).ToList();
        var counts = tabs.Select(t => { var mine = rows.Where(x => x.Tab == t.Name).ToList();
            return new PartsImportTabCount(t.Name, t.Family, mine.Count, mine.Count(x => x.Action == "Add"), mine.Count(x => x.Action == "Update"), mine.Count(x => x.Action == "Skip"), mine.Count(x => x.Action == "Error")); }).ToList();
        return plan with { Rows = rows, Tabs = counts, SkippedTabs = r.SkippedTabs ?? [] };
    }

    /// <summary>Applies a row to an existing part but keeps every value the part already has: only empty fields are filled in.</summary>
    static void FillBlanks(Action<Part> apply, Part t)
    {
        static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);
        var (name, desc, uom, pclass, mat, dwg, dwgRev, spec, lot, fam, typeId, hidden, brandId) = (t.Name, t.Description, t.UnitOfMeasure, t.PartClass, t.Material, t.DrawingNumber, t.DrawingRevision, t.Specification, t.LotControl, t.ProductFamily, t.TypeId, t.HiddenFields, t.BrandId);
        var (asm, rep, rp, lotOn, upc, upcKey) = (t.IsAssembly, t.IsRepairable, t.IsRepairPart, t.IsLotControlled, t.Upc, t.UpcKey);
        var attrs = PartAttributes.Read(t.Attributes).Where(kv => !PartFields.IsBlank(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        apply(t);
        if (!Blank(name)) t.Name = name;
        if (!Blank(desc)) t.Description = desc;
        if (!Blank(uom)) t.UnitOfMeasure = uom!;
        if (!Blank(pclass)) t.PartClass = pclass;
        if (!Blank(mat)) t.Material = mat;
        if (!PartFields.IsBlank(dwg)) t.DrawingNumber = dwg;
        if (!PartFields.IsBlank(dwgRev)) t.DrawingRevision = dwgRev;
        if (!Blank(spec)) t.Specification = spec;
        if (!Blank(lot)) { t.LotControl = lot; t.IsLotControlled = lotOn; }
        if (typeId is not null) { t.TypeId = typeId; t.ProductFamily = fam; }
        if (brandId is not null) t.BrandId = brandId;
        if (!Blank(upc)) { t.Upc = upc; t.UpcKey = upcKey; }
        // Yes/No flags have no empty state, so they are kept as they are
        t.IsAssembly = asm; t.IsRepairable = rep; t.IsRepairPart = rp;
        if (hidden is not null) t.HiddenFields = hidden;
        if (attrs.Count > 0) PartAttributes.Merge(t, attrs);
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
