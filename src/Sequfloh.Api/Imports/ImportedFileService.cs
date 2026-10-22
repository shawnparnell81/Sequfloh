using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Imports;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;

namespace Sequfloh.Imports;

public sealed record CellEdit(string Sheet, int R, int C, string? Value);
public sealed record ParagraphEdit(int Ref, string Text);

/// <summary>
/// Imported files: upload (Excel, CSV, Word, and legacy xls/doc through the office converter), the in-app view of a worksheet or document,
/// saving edits as a new version (formulas recalculated by the workbook engine), and downloads. Files go to the configured file storage.
/// </summary>
public sealed class ImportedFileService(AppDbContext db, IFileStorage storage, ILegacyOfficeConverter legacy, INumberingService numbers, IAccessService access, ICurrentUser me)
{
    public const long MaxBytes = 20 * 1024 * 1024;
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string XlsmType = "application/vnd.ms-excel.sheet.macroEnabled.12";
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly string[] Accepted = [".xlsx", ".xlsm", ".xls", ".csv", ".docx", ".doc"];

    Guid UserId => me.UserId ?? throw new ForbiddenException();

    async Task<ImportedFile> LoadAsync(Guid id, string permission, CancellationToken ct)
    {
        var f = await db.ImportedFiles.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Imported file");
        if (!await access.CanAccessAsync(UserId, Permissions.SequflohViewImportedFiles, f.SiteId, f.DepartmentId, ct)) throw new NotFoundException("Imported file");
        if (permission != Permissions.SequflohViewImportedFiles && !await access.CanAccessAsync(UserId, permission, f.SiteId, f.DepartmentId, ct)) throw new ForbiddenException();
        return f;
    }

    public async Task<object> ListAsync(string? q, string? kind, int page, int pageSize, CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(UserId, Permissions.SequflohViewImportedFiles, ct);
        if (scope.IsEmpty) return new { total = 0, items = Array.Empty<object>() };
        var query = db.ImportedFiles.AsNoTracking().Where(f => !f.IsArchived);
        if (!scope.AllSites)
        {
            var sites = scope.SiteIds.Concat(scope.DepartmentPairs.Select(p => p.Site)).Distinct().ToList();
            query = query.Where(f => sites.Contains(f.SiteId));
        }
        if (!string.IsNullOrWhiteSpace(q)) { var t = "%" + q.Trim() + "%"; query = query.Where(f => EF.Functions.ILike(f.Title, t) || EF.Functions.ILike(f.Number, t) || EF.Functions.ILike(f.OriginalFileName, t)); }
        if (kind == "spreadsheet") query = query.Where(f => f.Kind == ImportedFileKind.Spreadsheet);
        if (kind == "document") query = query.Where(f => f.Kind == ImportedFileKind.Document);
        var rows = await query.OrderByDescending(f => f.UpdatedAt)
            .Select(f => new { f.Id, f.Number, f.Title, f.Kind, f.SourceFormat, f.OriginalFileName, f.CurrentVersion, f.SiteId, f.DepartmentId, f.CreatedAt, f.UpdatedAt,
                UpdatedBy = db.Users.Where(u => u.Id == f.UpdatedById).Select(u => u.DisplayName).FirstOrDefault() })
            .ToListAsync(ct);
        var allowed = rows.Where(r => scope.Allows(r.SiteId, r.DepartmentId)).ToList();
        pageSize = Math.Clamp(pageSize, 1, 200); page = Math.Max(1, page);
        return new { total = allowed.Count, items = allowed.Skip((page - 1) * pageSize).Take(pageSize).Select(r => new { r.Id, r.Number, r.Title, Kind = r.Kind == ImportedFileKind.Spreadsheet ? "spreadsheet" : "document",
            r.SourceFormat, r.OriginalFileName, r.CurrentVersion, r.CreatedAt, r.UpdatedAt, r.UpdatedBy }) };
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohViewImportedFiles, ct);
        var versions = await db.ImportedFileVersions.AsNoTracking().Where(v => v.ImportedFileId == id).OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.FileName, v.SizeBytes, v.Note, v.CreatedAt, v.CreatedByName }).ToListAsync(ct);
        var canEdit = await access.CanAccessAsync(UserId, Permissions.SequflohImportFiles, f.SiteId, f.DepartmentId, ct);
        return new { f.Id, f.Number, f.Title, Kind = f.Kind == ImportedFileKind.Spreadsheet ? "spreadsheet" : "document", f.SourceFormat, f.OriginalFileName, f.CurrentVersion, f.RowVersion,
            f.CreatedAt, f.UpdatedAt, Summary = JsonDocument.Parse(f.Summary).RootElement, Versions = versions, CanEdit = canEdit };
    }

    // ---- upload -------------------------------------------------------------------------------------------------------------------

    public async Task<ImportedFile> UploadAsync(Stream content, string fileName, string? title, Guid? siteId, Guid? departmentId, CancellationToken ct)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!Accepted.Contains(ext)) throw new AppException("Upload an Excel (.xlsx, .xlsm, .xls), CSV or Word (.docx, .doc) file.");
        var original = new MemoryStream();
        await content.CopyToAsync(original, ct);
        if (original.Length == 0) throw new AppException("The file is empty.");
        if (original.Length > MaxBytes) throw new AppException("Files are limited to 20 MB.");
        original.Position = 0;
        if (await ImportSecurity.InspectAsync(original, ext, ct) is { } problem) throw new AppException(problem);

        var site = siteId ?? await db.Users.Where(u => u.Id == UserId).Select(u => u.DefaultSiteId).FirstOrDefaultAsync(ct) ?? await db.Sites.Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct)
            ?? throw new AppException("Set up a site first.");
        if (!await access.CanAccessAsync(UserId, Permissions.SequflohImportFiles, site, departmentId, ct)) throw new ForbiddenException();

        // the working copy that the app opens and edits
        byte[] working; string workingExt; string workingType; var kind = ext is ".docx" or ".doc" ? ImportedFileKind.Document : ImportedFileKind.Spreadsheet;
        switch (ext)
        {
            case ".csv": working = CsvToWorkbook(original, Path.GetFileNameWithoutExtension(name)); workingExt = ".xlsx"; workingType = XlsxType; break;
            case ".xls" or ".doc":
            {
                var to = ext == ".xls" ? "xlsx" : "docx";
                original.Position = 0;
                await using var converted = await legacy.ConvertAsync(original, ext.TrimStart('.'), to, ct);
                var ms = new MemoryStream(); await converted.CopyToAsync(ms, ct);
                working = ms.ToArray(); workingExt = "." + to; workingType = to == "xlsx" ? XlsxType : DocxType;
                break;
            }
            default: working = original.ToArray(); workingExt = ext; workingType = ext == ".docx" ? DocxType : ext == ".xlsm" ? XlsmType : XlsxType; break;
        }
        var summary = Summarize(kind, working, workingExt);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var f = new ImportedFile
        {
            Number = await numbers.NextAsync("SqfImportedFile", site, ct),
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(name) : title.Trim(),
            Kind = kind, SourceFormat = ext.TrimStart('.'), OriginalFileName = name, SiteId = site, DepartmentId = departmentId, Summary = summary,
        };
        if (f.Title.Length > 300) f.Title = f.Title[..300];
        f.OriginalStorageKey = $"imports/{f.Id:N}/original{ext}";
        original.Position = 0;
        await storage.PutAsync(f.OriginalStorageKey, original, ContentTypeOf(ext), ct);
        db.ImportedFiles.Add(f);
        db.ImportedFileVersions.Add(await StoreVersionAsync(f, 1, working, workingExt, workingType,
            ext is ".csv" or ".xls" or ".doc" ? $"Converted from {ext.TrimStart('.').ToUpperInvariant()}" : "As uploaded", ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return f;
    }

    static string ContentTypeOf(string ext) => ext switch
    {
        ".xlsx" => XlsxType, ".xlsm" => XlsmType, ".xls" => "application/vnd.ms-excel", ".csv" => "text/csv", ".docx" => DocxType, ".doc" => "application/msword", _ => "application/octet-stream",
    };

    async Task<ImportedFileVersion> StoreVersionAsync(ImportedFile f, int version, byte[] bytes, string ext, string type, string? note, CancellationToken ct)
    {
        var v = new ImportedFileVersion
        {
            ImportedFileId = f.Id, Version = version, FileName = Path.GetFileNameWithoutExtension(f.OriginalFileName) + ext, ContentType = type,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Note = note, CreatedByName = me.UserName,
        };
        v.StorageKey = $"imports/{f.Id:N}/v{version}{ext}";
        await storage.PutAsync(v.StorageKey, new MemoryStream(bytes), type, ct);
        return v;
    }

    /// <summary>Turns a CSV file into a one-sheet workbook: numbers and dates become typed cells, everything else stays text (text that looks like a formula is never run).</summary>
    public static byte[] CsvToWorkbook(Stream csv, string sheetName)
    {
        csv.Position = 0;
        var a = CsvParser.Analyze(csv, null);
        csv.Position = 0;
        using var reader = new StreamReader(csv, CsvParser.EncodingFor(a.Encoding), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName(sheetName));
        var widths = new Dictionary<int, int>();
        var r = 0;
        foreach (var row in CsvParser.Read(reader, a.Delimiter))
        {
            r++;
            for (var c = 0; c < row.Length; c++)
            {
                var v = row[c];
                var cell = ws.Cell(r, c + 1);
                if (v.Length == 0) continue;
                if (!(a.HasHeader && r == a.HeaderRow) && CsvParser.TryNumber(v, a.DecimalSeparator, out var d) && !v.Trim().StartsWith('+')) cell.Value = (double)d;
                else if (!(a.HasHeader && r == a.HeaderRow) && a.DateFormat is not null && CsvParser.TryDate(v, a.DateFormat, out var dt)) { cell.Value = dt; cell.Style.DateFormat.Format = "yyyy-mm-dd"; }
                else cell.Value = v;
                widths[c + 1] = Math.Max(widths.GetValueOrDefault(c + 1), Math.Min(60, v.Length));
            }
        }
        if (a.HasHeader && r >= a.HeaderRow) ws.Row(a.HeaderRow).Style.Font.Bold = true;
        foreach (var (c, w) in widths) ws.Column(c).Width = Math.Max(8, w + 2);
        var ms = new MemoryStream();
        wb.SaveAs(ms, new SaveOptions { EvaluateFormulasBeforeSaving = true });
        return ms.ToArray();
    }

    static string SheetName(string s)
    {
        var clean = new string(s.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim('\'').Trim();
        if (clean.Length == 0) clean = "Sheet1";
        return clean.Length > 31 ? clean[..31] : clean;
    }

    static string Summarize(ImportedFileKind kind, byte[] working, string ext)
    {
        if (kind == ImportedFileKind.Spreadsheet)
        {
            var wb = WorkbookReader.Read(new MemoryStream(working), ext.TrimStart('.'));
            var s = WorkbookIntake.Analyze(wb);
            return JsonSerializer.Serialize(new
            {
                s.Format, s.HasMacros, Sheets = s.Sheets.Select(x => new { x.Name, x.Visibility, x.Rows, x.Cols, x.Formulas, x.Merges, x.Charts, x.Images, x.Pivots, x.Protected }),
                s.FormulaTotal, s.FormulaClasses, s.Warnings, s.Security, s.ExternalLinks,
            }, Json);
        }
        var d = DocxReader.Read(new MemoryStream(working));
        return JsonSerializer.Serialize(new
        {
            d.Title, Paragraphs = d.Blocks.Count(b => b.P is not null), Tables = d.Tables.Count(), Controls = d.Controls.Count(), d.Images, d.Comments, d.TrackedChanges,
            Sections = d.Blocks.Count(b => b.Kind == "section") + 1, d.Warnings, d.Security, d.HasMacros,
        }, Json);
    }

    async Task<(ImportedFileVersion Version, byte[] Bytes)> CurrentAsync(ImportedFile f, int? version, CancellationToken ct)
    {
        var n = version ?? f.CurrentVersion;
        var v = await db.ImportedFileVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ImportedFileId == f.Id && x.Version == n, ct) ?? throw new NotFoundException("Version");
        await using var s = await storage.GetAsync(v.StorageKey, ct) ?? throw new NotFoundException("File content");
        var ms = new MemoryStream(); await s.CopyToAsync(ms, ct);
        return (v, ms.ToArray());
    }

    // ---- spreadsheets -------------------------------------------------------------------------------------------------------------

    public async Task<object> SheetAsync(Guid id, string? sheet, int? version, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohViewImportedFiles, ct);
        if (f.Kind != ImportedFileKind.Spreadsheet) throw new AppException("This imported file is a document.");
        var (v, bytes) = await CurrentAsync(f, version, ct);
        var wb = WorkbookReader.Read(new MemoryStream(bytes), Path.GetExtension(v.FileName).TrimStart('.'));
        var grid = WorkbookIntake.Grid(wb, sheet, 500, 60);
        var sh = wb.Sheet(grid.Sheet)!;
        var formulas = grid.Cells.Where(c => c.Formula is not null).Select(c => new { c.R, c.C, Classes = FormulaTranslator.Classify(c.Formula!, wb.HasMacros, out var warning), Warning = warning }).ToList();
        return new { Version = v.Version, grid.Sheet, grid.R1, grid.C1, grid.R2, grid.C2, grid.Widths, grid.Heights, grid.Cells, grid.Truncated, grid.Sheets,
            sh.FreezeRows, sh.FreezeCols, Formulas = formulas };
    }

    public async Task<object> SaveCellsAsync(Guid id, int baseVersion, IReadOnlyList<CellEdit> edits, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohImportFiles, ct);
        if (f.Kind != ImportedFileKind.Spreadsheet) throw new AppException("This imported file is a document.");
        if (f.IsArchived) throw new AppException("This imported file is archived.");
        if (baseVersion != f.CurrentVersion) throw new ConflictException($"Someone saved version {f.CurrentVersion} while you were editing. Reload the file and enter your changes again.");
        if (edits.Count == 0) throw new AppException("There are no changes to save.");
        if (edits.Count > 5000) throw new AppException("Save at most 5000 cells at a time.");
        var (v, bytes) = await CurrentAsync(f, null, ct);
        var (saved, recalculated) = ApplyCells(bytes, edits);
        return await SaveVersionAsync(f, v, saved, Describe(edits), recalculated ? null : "Formulas will be recalculated when the file is opened in Excel.", ct);
    }

    static string Describe(IReadOnlyList<CellEdit> edits)
    {
        var bySheet = edits.GroupBy(e => e.Sheet).Select(g => $"{g.Key}!{string.Join(", ", g.Take(8).Select(e => FormulaRefs.Address(e.R, e.C)))}{(g.Count() > 8 ? $" and {g.Count() - 8} more" : "")}");
        return $"Edited {edits.Count} cell{(edits.Count == 1 ? "" : "s")}: {string.Join("; ", bySheet)}";
    }

    /// <summary>Writes values and formulas into the workbook and recalculates it. Text starting with "=" is a formula; numbers, TRUE/FALSE and ISO dates are typed.</summary>
    public static (byte[] Bytes, bool Recalculated) ApplyCells(byte[] workbook, IEnumerable<CellEdit> edits)
    {
        using var wb = new XLWorkbook(new MemoryStream(workbook));
        foreach (var e in edits)
        {
            if (!wb.TryGetWorksheet(e.Sheet, out var ws)) throw new AppException($"The workbook has no worksheet named '{e.Sheet}'.");
            if (e.R < 1 || e.C < 1 || e.R > 1_048_576 || e.C > 16_384) throw new AppException("A cell is outside the worksheet.");
            var cell = ws.Cell(e.R, e.C);
            var v = e.Value ?? "";
            if (v.Length > 1 && v[0] == '=') cell.FormulaA1 = v[1..];
            else if (v.Length == 0) cell.Clear(XLClearOptions.Contents);
            else if (double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d)) cell.Value = d;
            else if (bool.TryParse(v, out var b)) cell.Value = b;
            else if (DateTime.TryParseExact(v, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                cell.Value = dt;
                if (cell.Style.NumberFormat.NumberFormatId == 0 && string.IsNullOrEmpty(cell.Style.NumberFormat.Format)) cell.Style.DateFormat.Format = "yyyy-mm-dd";
            }
            else cell.Value = v;
        }
        var recalculated = true;
        try { wb.RecalculateAllFormulas(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { recalculated = false; }
        if (!recalculated) wb.ForceFullCalculation = true;
        var ms = new MemoryStream();
        wb.SaveAs(ms, new SaveOptions { EvaluateFormulasBeforeSaving = true });
        return (ms.ToArray(), recalculated);
    }

    async Task<object> SaveVersionAsync(ImportedFile f, ImportedFileVersion from, byte[] bytes, string description, string? notice, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var next = f.CurrentVersion + 1;
        db.ImportedFileVersions.Add(await StoreVersionAsync(f, next, bytes, Path.GetExtension(from.FileName), from.ContentType, description.Length > 500 ? description[..497] + "..." : description, ct));
        f.CurrentVersion = next;
        f.Summary = Summarize(f.Kind, bytes, Path.GetExtension(from.FileName));
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Edited", EntityType = nameof(ImportedFile), EntityId = f.Id, SiteId = f.SiteId,
            CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Edited imported file {f.Number} {f.Title}, saved as version {next}. {description}"),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { Version = next, Notice = notice };
    }

    // ---- documents ----------------------------------------------------------------------------------------------------------------

    public async Task<object> DocumentAsync(Guid id, int? version, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohViewImportedFiles, ct);
        if (f.Kind != ImportedFileKind.Document) throw new AppException("This imported file is a spreadsheet.");
        var (v, bytes) = await CurrentAsync(f, version, ct);
        var d = DocxReader.Read(new MemoryStream(bytes));
        return new { Version = v.Version, d.Title, d.Blocks, d.Header, d.Footer, d.FinalSection, d.Images, d.Warnings, d.Security };
    }

    public async Task<object> SaveDocumentAsync(Guid id, int baseVersion, IReadOnlyList<ParagraphEdit> edits, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohImportFiles, ct);
        if (f.Kind != ImportedFileKind.Document) throw new AppException("This imported file is a spreadsheet.");
        if (f.IsArchived) throw new AppException("This imported file is archived.");
        if (baseVersion != f.CurrentVersion) throw new ConflictException($"Someone saved version {f.CurrentVersion} while you were editing. Reload the file and enter your changes again.");
        if (edits.Count == 0) throw new AppException("There are no changes to save.");
        var (v, bytes) = await CurrentAsync(f, null, ct);
        var model = DocxReader.Read(new MemoryStream(bytes));
        var editable = AllParagraphs(model).Where(p => p.Editable).Select(p => p.Ref!.Value).ToHashSet();
        if (edits.Any(e => !editable.Contains(e.Ref))) throw new AppException("A paragraph with fields or form controls cannot be edited here; download the file to change it.");
        var saved = DocxEditor.Apply(bytes, edits.GroupBy(e => e.Ref).ToDictionary(g => g.Key, g => g.Last().Text));
        return await SaveVersionAsync(f, v, saved, $"Edited {edits.Count} paragraph{(edits.Count == 1 ? "" : "s")}", null, ct);
    }

    static IEnumerable<WPara> AllParagraphs(DocModel m)
    {
        static IEnumerable<WPara> Of(WTable t) => t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Paras.Concat(c.Nested.SelectMany(Of)));
        foreach (var b in m.Blocks)
        {
            if (b.P is not null) yield return b.P;
            if (b.T is not null) foreach (var p in Of(b.T)) yield return p;
        }
    }

    // ---- files --------------------------------------------------------------------------------------------------------------------

    public async Task<(Stream Content, string ContentType, string FileName)> DownloadAsync(Guid id, int? version, bool original, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohViewImportedFiles, ct);
        if (original)
            return (await storage.GetAsync(f.OriginalStorageKey, ct) ?? throw new NotFoundException("File content"), ContentTypeOf(Path.GetExtension(f.OriginalFileName).ToLowerInvariant()), f.OriginalFileName);
        var (v, bytes) = await CurrentAsync(f, version, ct);
        var name = version is null || version == f.CurrentVersion ? v.FileName : $"{Path.GetFileNameWithoutExtension(v.FileName)} (version {v.Version}){Path.GetExtension(v.FileName)}";
        return (new MemoryStream(bytes), v.ContentType, name);
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohImportFiles, ct);
        if (string.IsNullOrWhiteSpace(title)) throw new AppException("Enter a title.");
        f.Title = title.Trim().Length > 300 ? title.Trim()[..300] : title.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        var f = await LoadAsync(id, Permissions.SequflohImportFiles, ct);
        f.IsArchived = true; f.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
