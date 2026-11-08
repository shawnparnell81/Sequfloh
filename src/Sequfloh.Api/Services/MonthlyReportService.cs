using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Platform;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed class MonthlyReportService(AppDbContext db, ModuleSupport m, SequflohFoundationService foundation, IFileStorage storage)
{
    const string Entity = nameof(MonthlyQualityReport);
    public const long MaxBytes = 20 * 1024 * 1024;

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsView, ct);
        var rows = await db.MonthlyQualityReports.AsNoTracking().OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToListAsync(ct);
        return rows.Select(r => (object)new
        {
            r.Id, r.Year, r.Month, Title = MonthlyReportLayout.Title(r.Year, r.Month), r.Status, r.FlatRate, r.UpdatedAt, r.CreatedByName, r.FinalizedAt, r.FinalizedByName,
        }).ToList();
    }

    public object Sources() => MonthlySourceCatalog.All.Select(p => new { p.Type, p.Label, p.Hint, p.Ready, extensions = p.Extensions, fills = p.Fills });

    public async Task<IReadOnlyList<object>> PeopleAsync(CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsView, ct);
        return await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => (object)new { u.Id, u.DisplayName }).ToListAsync(ct);
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsView, ct);
        var report = await LoadAsync(id, ct);
        return await DetailAsync(report, ct);
    }

    public async Task<object> CreateAsync(int year, int month, bool copyText, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsEdit, ct);
        CheckMonth(year, month);
        if (await db.MonthlyQualityReports.AnyAsync(r => r.Year == year && r.Month == month, ct))
            throw new ConflictException($"A report for {MonthlyReportLayout.Name(month)} {year} already exists.");
        var body = new MonthlyReportBody { DocumentDate = MonthlyReportIdentity.LastDay(year, month) };
        var copied = false;
        var prev = Previous(year, month);
        var earlier = await db.MonthlyQualityReports.AsNoTracking().FirstOrDefaultAsync(r => r.Year == prev.Year && r.Month == prev.Month, ct);
        if (copyText && earlier is not null)
        {
            MonthlyReportMath.CopyText(Read(earlier), body);
            body.DocumentDate = MonthlyReportIdentity.LastDay(year, month);
            copied = true;
        }
        var report = new MonthlyQualityReport
        {
            Year = year, Month = month, Status = "Draft", FlatRate = 75m, Body = Write(body), CreatedByName = m.Current.UserName,
        };
        db.MonthlyQualityReports.Add(report);
        var title = MonthlyReportLayout.Title(year, month);
        foundation.Audit("MonthlyReportCreated", Entity, report.Id,
            copied ? $"Created {title}, copying the text from {MonthlyReportLayout.Title(earlier!.Year, earlier.Month)}." : $"Created {title}.");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(report, ct);
    }

    public async Task<object> UpdateAsync(Guid id, MonthlyReportBody incoming, decimal flatRate, uint rowVersion, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsEdit, ct);
        var report = await LoadAsync(id, ct);
        EnsureDraft(report);
        if (flatRate <= 0 || flatRate > 100000) throw new AppException("The flat rate must be greater than zero.");
        var before = Read(report);
        var clean = Clean(incoming, before);
        if (clean.ApprovedById is Guid approver)
        {
            var name = await db.Users.AsNoTracking().Where(u => u.Id == approver && u.IsActive).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(name)) throw new AppException("Choose an active person for Approved By.");
            clean.ApprovedByName = name;
        }
        else clean.ApprovedByName = "";
        var rateBefore = report.FlatRate;
        foreach (var (key, origin) in clean.Origin)
        {
            var now = MonthlyReportMath.CanonOf(clean, key);
            var was = MonthlyReportMath.CanonOf(before, key);
            if (now == was) { origin.Overridden = now != origin.Computed && origin.Computed.Length > 0; continue; }
            origin.Overridden = now != origin.Computed;
            var label = MonthlyReportFields.Labels.TryGetValue(key, out var nice) ? nice : key;
            if (origin.Overridden)
                foundation.Audit("MonthlyReportOverridden", Entity, report.Id,
                    $"Changed {label} on {MonthlyReportLayout.Title(report.Year, report.Month)} from {Display(origin.Computed)} (imported from {origin.FileName}) to {Display(now)}.");
            else if (was != origin.Computed && now == origin.Computed)
                foundation.Audit("MonthlyReportRestored", Entity, report.Id,
                    $"Restored {label} on {MonthlyReportLayout.Title(report.Year, report.Month)} to {Display(origin.Computed)}, the value imported from {origin.FileName}.");
        }
        foreach (var heading in MonthlyReportMath.ChangedSections(before, clean, rateBefore, flatRate))
            foundation.Audit("MonthlyReportEdited", Entity, report.Id,
                heading == "document header"
                    ? $"Edited the document header on {MonthlyReportLayout.Title(report.Year, report.Month)}."
                    : $"Edited section {heading} on {MonthlyReportLayout.Title(report.Year, report.Month)}.");
        report.FlatRate = flatRate;
        report.Body = Write(clean);
        if (rowVersion != 0) db.Entry(report).Property(x => x.RowVersion).OriginalValue = rowVersion;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Someone else saved this report. Reload it and try again."); }
        return await DetailAsync(report, ct);
    }

    public async Task<object> UploadAsync(Guid id, string type, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsEdit, ct);
        var report = await LoadAsync(id, ct);
        EnsureDraft(report);
        var parser = MonthlySourceCatalog.Find(type) ?? ImageKind(type) ?? throw new AppException("Choose a source file type.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (bytes.Length == 0) throw new AppException("Choose a file.");
        if (bytes.Length > MaxBytes) throw new AppException("Files are limited to 20 MB.");
        if (parser is ImageSlot slot)
        {
            if (!slot.Extensions.Contains(ext)) throw new AppException("Use a PNG, JPEG or WebP picture.");
            var jpeg = ReportPhoto.BaselineJpeg(bytes, out var error) ?? throw new AppException(error ?? "That file is not a picture the report can show.");
            var stored = await StoreAsync(report, ext, jpeg, string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType, fileName, slot.Type, ct);
            var body = Read(report);
            SetImage(body, slot.Type, stored.StorageKey);
            report.Body = Write(body);
            foundation.Audit("MonthlyReportFileUploaded", Entity, report.Id,
                $"Uploaded {slot.Label} {Path.GetFileName(fileName)} for {MonthlyReportLayout.Title(report.Year, report.Month)}.");
            await db.SaveChangesAsync(ct);
            return await DetailAsync(report, ct);
        }
        if (!parser.Extensions.Contains(ext)) throw new AppException($"Use a {string.Join(" or ", parser.Extensions)} file for {parser.Label}.");
        SourceImport import;
        await using (var ms = new MemoryStream(bytes))
            import = parser.Read(ms, report.Year, report.Month);
        var file = await StoreAsync(report, ext, bytes, contentType, fileName, parser.Type, ct);
        file.Result = JsonSerializer.Serialize(new
        {
            import.Applied, import.Note, import.IncludedRows, import.DistinctCount, import.SkippedTotal, import.OutsideMonth,
            import.Warnings, import.Skipped, tabs = import.Tabs.Select(t => new { t.Tab, t.HeaderRow, t.Headers }),
        }, SequflohJson.Options);
        if (import.Applied)
        {
            var body = Read(report);
            var kept = new List<string>();
            foreach (var (key, canon) in import.Fields)
            {
                body.Origin.TryGetValue(key, out var existing);
                if (existing?.Overridden == true)
                {
                    existing.Computed = canon;
                    existing.FileId = file.Id;
                    existing.FileName = file.FileName;
                    existing.FileType = import.FileType;
                    existing.Tab = import.FieldTabs.TryGetValue(key, out var keptTab) ? keptTab : import.Tab;
                    kept.Add(MonthlyReportFields.Labels.TryGetValue(key, out var nice) ? nice : key);
                    continue;
                }
                MonthlyReportMath.Assign(body, key, canon, import.FirstArticle, import.TopParts, import.Vehicles);
                body.Origin[key] = new FieldOrigin
                {
                    FileType = import.FileType, FileName = file.FileName, FileId = file.Id,
                    Tab = import.FieldTabs.TryGetValue(key, out var tab) ? tab : import.Tab, Computed = canon, Overridden = false,
                };
            }
            report.Body = Write(body);
            var filled = import.Fields.Keys.Select(k => MonthlyReportFields.Labels.TryGetValue(k, out var nice) ? nice : k).ToList();
            var sentence = filled.Count == 0
                ? $"Uploaded {parser.Label} {file.FileName} for {MonthlyReportLayout.Title(report.Year, report.Month)}. No totals were filled."
                : $"Uploaded {parser.Label} {file.FileName} for {MonthlyReportLayout.Title(report.Year, report.Month)} and filled {string.Join(", ", filled)}.";
            if (kept.Count > 0) sentence += $" Left the overridden {(kept.Count == 1 ? "value" : "values")} for {string.Join(", ", kept)}.";
            foundation.Audit("MonthlyReportImported", Entity, report.Id, sentence);
        }
        else
        {
            foundation.Audit("MonthlyReportFileUploaded", Entity, report.Id,
                $"Uploaded {parser.Label} {file.FileName} for {MonthlyReportLayout.Title(report.Year, report.Month)}. {import.Note}");
        }
        await db.SaveChangesAsync(ct);
        return await DetailAsync(report, ct);
    }

    public async Task<object> FillAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsEdit, ct);
        var report = await LoadAsync(id, ct);
        EnsureDraft(report);
        var body = Read(report);
        var notes = new List<string>();
        var (start, end) = (new DateOnly(report.Year, report.Month, 1), new DateOnly(report.Year, report.Month, 1).AddMonths(1));
        var (from, to) = await m.DayRangeAsync(null, start, end.AddDays(-1), ct);
        var records = await db.SequflohTrackingRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .Select(r => new Wo(r.Id, r.Type, r.Status, r.PartId, r.PartNumber, r.PartDescription, r.FamilyName, r.InspectionSubType, r.Result, r.Deviations, r.Quarantine, r.QuarantineReason,
                r.NonconformanceReferences, r.ObservedCondition, r.FailureReason, r.InspectionDescription, r.FinalDisposition, r.ActualCompletion, r.CreatedAt, r.UseCrewTime))
            .ToListAsync(ct);
        var partIds = records.Where(r => r.PartId is not null).Select(r => r.PartId!.Value).Distinct().ToList();
        var parts = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PartNumber, p.Name, p.ProductFamily }).ToListAsync(ct);
        var partOf = parts.ToDictionary(p => p.Id);

        if (!body.Origin.ContainsKey(MonthlyReportFields.TotalClaimsProcessed) && body.TotalClaimsProcessed is null)
        {
            var claims = records.Where(IsWarranty).ToList();
            body.TotalClaimsProcessed = claims.Count;
            Note(body, MonthlyReportFields.TotalClaimsProcessed, claims.Count.ToString(CultureInfo.InvariantCulture), "Sequfloh warranty work orders");
            notes.Add($"Tracker processed claims filled from {claims.Count} Sequfloh warranty work orders. The raw export count was not changed.");
            if (!body.Origin.ContainsKey(MonthlyReportFields.DmaParts) && body.DmaParts is null && body.CarterParts is null)
            {
                var dma = 0;
                var carter = 0;
                foreach (var wo in claims)
                {
                    if (wo.PartId is not { } pid || !partOf.TryGetValue(pid, out var part)) continue;
                    var family = part.ProductFamily ?? "";
                    if (family.Contains("fuel pump", StringComparison.OrdinalIgnoreCase) != true) continue;
                    var blob = $"{part.PartNumber} {part.Name} {family}";
                    if (System.Text.RegularExpressions.Regex.IsMatch(blob, @"\bDMA\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) dma++;
                    else carter++;
                }
                if (dma + carter > 0)
                {
                    body.DmaParts = dma;
                    body.CarterParts = carter;
                    Note(body, MonthlyReportFields.DmaParts, dma.ToString(CultureInfo.InvariantCulture), "Sequfloh parts");
                    Note(body, MonthlyReportFields.CarterParts, carter.ToString(CultureInfo.InvariantCulture), "Sequfloh parts");
                    notes.Add("Fuel pump split filled from warranty work orders whose part family is Fuel Pump.");
                }
            }
            if (body.TopParts.Count == 0)
            {
                body.TopParts = claims.GroupBy(c => (c.PartNumber ?? "").Trim()).Where(g => g.Key.Length > 0)
                    .Select(g => new PartClaimRow { PartNumber = g.Key, Description = g.Select(x => x.PartDescription).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "", TotalClaims = g.Count() })
                    .OrderByDescending(p => p.TotalClaims).ThenBy(p => p.PartNumber, StringComparer.Ordinal).Take(10).ToList();
                if (body.TopParts.Count > 0) notes.Add("Top returning parts filled from warranty work orders. This is the work-order count, not the raw claim export.");
            }
        }

        if (!body.Origin.ContainsKey(MonthlyReportFields.AlertsTotal) && body.AlertsTotal is null)
        {
            var alerts = records.Where(r => r.InspectionSubType is not null && r.InspectionSubType.Contains("product alert", StringComparison.OrdinalIgnoreCase)).ToList();
            if (alerts.Count > 0)
            {
                body.AlertsTotal = alerts.Count;
                body.AlertsOpen = alerts.Count(a => a.Status is not TrackingStatus.Completed and not TrackingStatus.Failed and not TrackingStatus.Canceled);
                Note(body, MonthlyReportFields.AlertsTotal, body.AlertsTotal.Value.ToString(CultureInfo.InvariantCulture), "Sequfloh product alert work orders");
                Note(body, MonthlyReportFields.AlertsOpen, body.AlertsOpen.Value.ToString(CultureInfo.InvariantCulture), "Sequfloh product alert work orders");
                var ids = alerts.Select(a => a.Id).ToList();
                var userSeconds = await db.SequflohTimeEntries.Where(t => ids.Contains(t.TrackingRecordId)).SumAsync(t => (long?)t.Seconds, ct) ?? 0;
                var sessions = await db.SequflohCrewSessions.Where(s => ids.Contains(s.TrackingRecordId)).ToListAsync(ct);
                var crewSeconds = sessions.Where(s => s.WorkerId is not null || alerts.First(a => a.Id == s.TrackingRecordId).UseCrewTime).Sum(s => SequflohCrewService.Labor(s, m.Now));
                if (userSeconds + crewSeconds > 0 && body.HoursInvestigating is null)
                {
                    body.HoursInvestigating = Math.Round((userSeconds + crewSeconds) / 3600m, 2, MidpointRounding.AwayFromZero);
                    Note(body, MonthlyReportFields.HoursInvestigating, MonthlyReportMath.Canon(body.HoursInvestigating), "Sequfloh time entries");
                }
                var closed = alerts.Where(a => a.ActualCompletion is not null).ToList();
                if (closed.Count > 0 && body.AvgDaysToClose is null)
                {
                    body.AvgDaysToClose = Math.Round((decimal)closed.Average(a => (a.ActualCompletion!.Value - a.CreatedAt).TotalDays), 2, MidpointRounding.AwayFromZero);
                    Note(body, MonthlyReportFields.AvgDaysToClose, MonthlyReportMath.Canon(body.AvgDaysToClose), "Sequfloh product alert work orders");
                }
                notes.Add($"Product alerts filled from {alerts.Count} Sequfloh work orders tagged Product alert.");
            }
        }

        if (!body.Origin.ContainsKey(MonthlyReportFields.FirstArticle) && body.FirstArticle.Count == 0)
        {
            var fai = records.Where(r => r.Type is TrackingType.Validation or TrackingType.Fai && (r.ActualCompletion is { } done && done >= from && done < to || r.Status == TrackingStatus.Completed)).ToList();
            if (fai.Count > 0)
            {
                var buckets = new Dictionary<string, FaiRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var wo in fai)
                {
                    var category = string.IsNullOrWhiteSpace(wo.FamilyName) ? "Unassigned" : wo.FamilyName.Trim();
                    if (!buckets.TryGetValue(category, out var row)) buckets[category] = row = new FaiRow { Category = category };
                    row.TotalCompleted++;
                    var disp = EngineeringFaiParser.Disposition(string.IsNullOrWhiteSpace(wo.Deviations) ? wo.Result : "Pass with Deviation");
                    if (!string.IsNullOrWhiteSpace(wo.Deviations)) disp = "deviation";
                    else if (wo.Status == TrackingStatus.Failed) disp = "failed";
                    if (disp == "passed") row.Passed++;
                    else if (disp == "failed") row.Failed++;
                    else if (disp == "deviation") row.PassedWithDeviation++;
                }
                body.FirstArticle = buckets.Values.OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase).ToList();
                Note(body, MonthlyReportFields.FirstArticle, MonthlyReportMath.Canon(body.FirstArticle), "Sequfloh validation and FAI work orders");
                notes.Add("First article rows filled from completed Sequfloh validation and FAI work orders, one row per work order.");
            }
        }

        if (body.TotalAmountRequested is null && body.PartsAmountRequested is null && body.LaborAmountRequested is null)
        {
            var chargebacks = await db.SequflohChargebackReports.AsNoTracking().Where(c => c.CreatedAt >= from && c.CreatedAt < to).ToListAsync(ct);
            if (chargebacks.Count > 0)
            {
                decimal partsCost = 0, labor = 0, other = 0;
                foreach (var c in chargebacks)
                {
                    var partLines = JsonSerializer.Deserialize<List<ChargebackPart>>(c.Parts, SequflohJson.Options) ?? [];
                    var lines = JsonSerializer.Deserialize<List<CostLine>>(c.CostLines, SequflohJson.Options) ?? [];
                    partsCost += SequflohChargebackService.PartsCost(partLines);
                    labor += SequflohChargebackService.LaborCost(c.TotalHours, c.HourlyRate) ?? 0;
                    other += lines.Sum(l => l.Amount);
                }
                body.PartsAmountRequested = Math.Round(partsCost, 2);
                body.LaborAmountRequested = Math.Round(labor, 2);
                body.TotalAmountRequested = Math.Round(partsCost + labor + other, 2);
                notes.Add($"Amounts requested filled from {chargebacks.Count} Sequfloh chargeback reports. These are chargeback costs, not an O'Reilly raw export.");
            }
        }

        if (string.IsNullOrWhiteSpace(body.Quarantine))
        {
            var held = records.Where(r => r.Quarantine).ToList();
            if (held.Count > 0)
            {
                body.Quarantine = string.Join("\n", held.Select(r => $"{(string.IsNullOrWhiteSpace(r.PartNumber) ? r.Id.ToString() : r.PartNumber)}: {(string.IsNullOrWhiteSpace(r.QuarantineReason) ? "flagged quarantine" : r.QuarantineReason.Trim())}"));
                notes.Add("Quarantine text filled from work orders flagged quarantine. A failed inspection was not treated as quarantine.");
            }
        }

        if (body.Ncrs.Count == 0 && body.NcrCount is null)
        {
            var ncrs = records.Where(r => !string.IsNullOrWhiteSpace(r.NonconformanceReferences)).ToList();
            if (ncrs.Count > 0)
            {
                body.Ncrs = ncrs.Select(r => new NcrRow
                {
                    Number = r.NonconformanceReferences!.Trim(),
                    PartNumber = r.PartNumber ?? "",
                    Defect = FirstText(r.ObservedCondition, r.FailureReason, r.InspectionDescription),
                    Disposition = r.FinalDisposition ?? "",
                    Status = r.Status is TrackingStatus.Completed or TrackingStatus.Canceled ? "Closed" : "Open",
                }).ToList();
                body.NcrCount = body.Ncrs.Count;
                body.NcrOpen = body.Ncrs.Count(n => n.Status == "Open");
                body.NcrClosed = body.Ncrs.Count(n => n.Status == "Closed");
                notes.Add("NCR rows filled from work orders that already have a nonconformance reference. CAR and RPN counts were not changed.");
            }
        }

        if (notes.Count == 0) notes.Add("Nothing in Sequfloh matched an empty field for this month. Typed values and imported values were left as they are.");
        report.Body = Write(body);
        foundation.Audit("MonthlyReportFilled", Entity, report.Id, $"Filled {MonthlyReportLayout.Title(report.Year, report.Month)} from Sequfloh. {string.Join(" ", notes)}");
        await db.SaveChangesAsync(ct);
        return new { report = await DetailAsync(report, ct), notes };
    }

    public async Task<object> FinalizeAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsFinalize, ct);
        var report = await LoadAsync(id, ct);
        if (report.Status == "Final") throw new AppException("This report is already final.");
        report.Status = "Final";
        report.FinalizedAt = m.Now;
        report.FinalizedById = m.UserId;
        report.FinalizedByName = m.Current.UserName;
        foundation.Audit("MonthlyReportFinalized", Entity, report.Id, $"Finalized {MonthlyReportLayout.Title(report.Year, report.Month)}.");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(report, ct);
    }

    public async Task<object> ReopenAsync(Guid id, string? reason, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsFinalize, ct);
        var report = await LoadAsync(id, ct);
        if (report.Status != "Final") throw new AppException("This report is not final.");
        var why = (reason ?? "").Trim();
        if (why.Length == 0) throw new AppException("Enter a reason to reopen the report.");
        report.Status = "Draft";
        report.ReopenedAt = m.Now;
        report.ReopenedById = m.UserId;
        report.ReopenReason = why.Length > 2000 ? why[..2000] : why;
        foundation.Audit("MonthlyReportReopened", Entity, report.Id, $"Reopened {MonthlyReportLayout.Title(report.Year, report.Month)}. Reason: {report.ReopenReason}", reason: report.ReopenReason);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(report, ct);
    }

    public async Task<(byte[] Bytes, string Name)> PdfAsync(Guid id, CancellationToken ct) => await ExportAsync(id, pdf: true, ct);
    public async Task<(byte[] Bytes, string Name)> DocxAsync(Guid id, CancellationToken ct) => await ExportAsync(id, pdf: false, ct);

    public async Task<byte[]> ImageAsync(Guid id, string slot, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsView, ct);
        var report = await LoadAsync(id, ct);
        var body = Read(report);
        var key = ImageKey(body, slot);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var stream = await storage.GetAsync(key, ct);
            if (stream is not null)
            {
                await using (stream)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    var jpeg = ReportPhoto.BaselineJpeg(ms.ToArray(), out _);
                    if (jpeg is not null) return jpeg;
                }
            }
        }
        var images = await PicturesAsync(report, body, await TrendOf(report, body, ct), ct);
        return images.TryGetValue(slot, out var generated) ? generated : MonthlyReportCharts.Note(slot, "No picture.");
    }

    async Task<(byte[] Bytes, string Name)> ExportAsync(Guid id, bool pdf, CancellationToken ct)
    {
        await RequireAsync(Permissions.SequflohMonthlyReportsView, ct);
        var report = await LoadAsync(id, ct);
        var body = Read(report);
        var trend = await TrendOf(report, body, ct);
        var blocks = MonthlyReportLayout.Build(report.Year, report.Month, body, report.FlatRate, trend);
        var images = await PicturesAsync(report, body, trend, ct);
        var title = MonthlyReportLayout.Title(report.Year, report.Month);
        var identity = MonthlyReportIdentity.From(body, report.Year, report.Month);
        var bytes = pdf ? MonthlyReportExport.Pdf(identity, blocks, images) : MonthlyReportExport.Docx(identity, blocks, images);
        foundation.Audit(pdf ? "MonthlyReportExportedPdf" : "MonthlyReportExportedDocx", Entity, report.Id,
            pdf ? $"Exported {title} as a PDF." : $"Exported {title} as a Word document.");
        await db.SaveChangesAsync(ct);
        var safe = title.Replace(" ", "_");
        return (bytes, pdf ? safe + ".pdf" : safe + ".docx");
    }

    async Task<Dictionary<string, byte[]>> PicturesAsync(MonthlyQualityReport report, MonthlyReportBody body, List<TrendMonth> trend, CancellationToken ct)
    {
        var map = new Dictionary<string, byte[]>();
        async Task Put(string slot, string? key, byte[] generated)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                var stream = await storage.GetAsync(key, ct);
                if (stream is not null)
                {
                    await using (stream)
                    {
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        var jpeg = ReportPhoto.BaselineJpeg(ms.ToArray(), out _);
                        if (jpeg is not null) { map[slot] = jpeg; return; }
                    }
                }
            }
            map[slot] = generated;
        }
        await Put("trend", body.TrendImageKey, MonthlyReportCharts.Trend(trend));
        await Put("high-return", body.HighReturnImageKey, MonthlyReportCharts.HighReturn(body.TopParts.Select(p => (p.PartNumber, (double)p.TotalClaims)).ToList()));
        await Put("vehicles", body.VehicleImageKey, MonthlyReportCharts.Vehicles(body.VehicleModels.Select(v => (v.Name, (double)v.Count)).ToList()));
        await Put("email-issues", body.EmailImageKey, MonthlyReportCharts.Note("OR/NAPA email issues", "No email issues picture uploaded."));
        return map;
    }

    async Task<List<TrendMonth>> TrendOf(MonthlyQualityReport report, MonthlyReportBody body, CancellationToken ct)
    {
        var window = MonthlyReportMath.Window(report.Year, report.Month);
        var keys = window.Select(w => w.Year * 100 + w.Month).ToHashSet();
        var rows = await db.MonthlyQualityReports.AsNoTracking().Where(r => r.Id != report.Id).ToListAsync(ct);
        var saved = rows.Where(r => keys.Contains(r.Year * 100 + r.Month)).Select(r => (r.Year, r.Month, Read(r))).ToList();
        return MonthlyReportMath.Trend(report.Year, report.Month, body, saved);
    }

    async Task<object> DetailAsync(MonthlyQualityReport report, CancellationToken ct)
    {
        var body = Read(report);
        var trend = await TrendOf(report, body, ct);
        var blocks = MonthlyReportLayout.Build(report.Year, report.Month, body, report.FlatRate, trend);
        var files = await db.MonthlyReportFiles.AsNoTracking().Where(f => f.ReportId == report.Id).OrderByDescending(f => f.CreatedAt).ToListAsync(ct);
        return new
        {
            report.Id, report.Year, report.Month, Title = MonthlyReportLayout.Title(report.Year, report.Month), report.Status, report.FlatRate, report.RowVersion,
            report.CreatedAt, report.CreatedByName, report.UpdatedAt, report.FinalizedAt, report.FinalizedByName, report.ReopenedAt, report.ReopenReason,
            body,
            trend = trend.Select(t => new { t.Year, t.Month, t.Label, t.Current, t.HasReport, t.TotalClaims, t.ProductAlerts, t.TotalAmount, t.PartsAmount, t.LaborAmount, t.Ratio, t.Mttf, t.Median }),
            outline = blocks.Select(Block),
            files = files.Select(f => new { f.Id, f.FileType, f.FileName, f.SizeBytes, uploadedAt = f.CreatedAt, f.UploadedByName, f.ReportingCutoff, result = ParseResult(f.Result) }),
            calculated = new
            {
                potentialLiability = MonthlyReportMath.Liability(body.TotalClaimsRaw, report.FlatRate),
                approvedLiability = MonthlyReportMath.Liability(body.ClaimsApproved, report.FlatRate),
                deniedSavings = MonthlyReportMath.Liability(body.ClaimsDenied, report.FlatRate),
                ratio = MonthlyReportMath.Ratio(body.LaborAmountRequested, body.PartsAmountRequested),
                faiTotal = MonthlyReportMath.FaiTotal(body.FirstArticle),
            },
        };
    }

    static object Block(ReportBlock b) => b switch
    {
        ReportTitle t => new { kind = "title", text = t.Text },
        ReportHeading h => new { kind = h.Level == 2 ? "h2" : "h3", text = h.Text },
        ReportLine l => new { kind = "line", text = l.Text },
        ReportBullets x => new { kind = "bullets", items = x.Items },
        ReportTable t => new { kind = "table", headers = t.Headers, rows = t.Rows },
        ReportPicture p => new { kind = "image", slot = p.Slot },
        ReportStatus s => new { kind = "status", text = s.Value },
        _ => new { kind = "line", text = "" },
    };

    static JsonElement ParseResult(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return JsonSerializer.Deserialize<JsonElement>("{}"); }
    }

    async Task<MonthlyReportFile> StoreAsync(MonthlyQualityReport report, string ext, byte[] bytes, string? contentType, string fileName, string fileType, CancellationToken ct)
    {
        var file = new MonthlyReportFile
        {
            ReportId = report.Id, Year = report.Year, Month = report.Month, FileType = fileType,
            FileName = Path.GetFileName(fileName), ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = bytes.Length, UploadedByName = m.Current.UserName,
            ReportingCutoff = new DateOnly(report.Year, report.Month, 1).AddMonths(1),
        };
        file.StorageKey = $"monthly-reports/{report.Id:N}/{file.Id:N}{ext}";
        await using var ms = new MemoryStream(bytes);
        await storage.PutAsync(file.StorageKey, ms, file.ContentType, ct);
        db.MonthlyReportFiles.Add(file);
        return file;
    }

    static void SetImage(MonthlyReportBody body, string type, string key)
    {
        switch (type)
        {
            case "chart-trend": body.TrendImageKey = key; break;
            case "chart-high-return": body.HighReturnImageKey = key; break;
            case "chart-vehicles": body.VehicleImageKey = key; break;
            case "email-issues": body.EmailImageKey = key; break;
        }
    }

    static string? ImageKey(MonthlyReportBody body, string slot) => slot switch
    {
        "trend" => body.TrendImageKey,
        "high-return" => body.HighReturnImageKey,
        "vehicles" => body.VehicleImageKey,
        "email-issues" => body.EmailImageKey,
        _ => null,
    };

    static ImageSlot? ImageKind(string type) => type switch
    {
        "chart-trend" => new ImageSlot("chart-trend", "trend chart", [".png", ".jpg", ".jpeg", ".webp"]),
        "chart-high-return" => new ImageSlot("chart-high-return", "high-return chart", [".png", ".jpg", ".jpeg", ".webp"]),
        "chart-vehicles" => new ImageSlot("chart-vehicles", "vehicle model chart", [".png", ".jpg", ".jpeg", ".webp"]),
        "email-issues" => new ImageSlot("email-issues", "OR/NAPA email issues picture", [".png", ".jpg", ".jpeg", ".webp"]),
        _ => null,
    };

    static void Note(MonthlyReportBody body, string key, string computed, string fileName) =>
        body.Origin[key] = new FieldOrigin { FileType = "sequfloh", FileName = fileName, Tab = "Sequfloh", Computed = computed };

    static bool IsWarranty(Wo r) => r.Type == TrackingType.Warranty || (r.InspectionSubType?.Contains("warranty", StringComparison.OrdinalIgnoreCase) ?? false);
    static string FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
    static string Display(string canon) => canon.Length == 0 ? "blank" : canon;

    static MonthlyReportBody Clean(MonthlyReportBody incoming, MonthlyReportBody stored)
    {
        incoming.Origin = stored.Origin;
        incoming.CriticalRisks ??= [];
        incoming.TopParts ??= [];
        incoming.VehicleModels ??= [];
        incoming.RootCause ??= [];
        incoming.Recommendations ??= [];
        incoming.FirstArticle ??= [];
        incoming.Ncrs ??= [];
        incoming.Fitment ??= [];
        incoming.ProductInfo ??= [];
        incoming.TrendImageKey = stored.TrendImageKey;
        incoming.HighReturnImageKey = stored.HighReturnImageKey;
        incoming.VehicleImageKey = stored.VehicleImageKey;
        incoming.EmailImageKey = stored.EmailImageKey;
        incoming.DocumentId = Trim(string.IsNullOrWhiteSpace(incoming.DocumentId) ? MonthlyReportIdentity.DefaultId : incoming.DocumentId, 40);
        incoming.DocumentRevision = Trim(string.IsNullOrWhiteSpace(incoming.DocumentRevision) ? MonthlyReportIdentity.DefaultRevision : incoming.DocumentRevision, 20);
        incoming.DocumentTitle = Trim(string.IsNullOrWhiteSpace(incoming.DocumentTitle) ? MonthlyReportIdentity.DefaultTitle : incoming.DocumentTitle, 200);
        incoming.DocumentDate = string.IsNullOrWhiteSpace(incoming.DocumentDate) ? stored.DocumentDate : incoming.DocumentDate.Trim();
        incoming.DepartmentStatus = (incoming.DepartmentStatus ?? "").Trim();
        if (incoming.DepartmentStatus.Length > 0 && incoming.DepartmentStatus is not ("Green" or "Yellow" or "Red"))
            throw new AppException("Status of Department must be Green, Yellow or Red.");
        incoming.PrimaryAchievement = Trim(incoming.PrimaryAchievement, 8000);
        incoming.AlertNotes = Trim(incoming.AlertNotes, 8000);
        incoming.Quarantine = Trim(incoming.Quarantine, 8000);
        incoming.Recalls = Trim(incoming.Recalls, 8000);
        incoming.Collabtic = Trim(incoming.Collabtic, 8000);
        incoming.FieldQuestion = Trim(incoming.FieldQuestion, 8000);
        incoming.BlackLabelPallets = Trim(incoming.BlackLabelPallets, 8000);
        incoming.ClaimsPendingNote = Trim(incoming.ClaimsPendingNote, 2000);
        incoming.FitmentIntro = Trim(incoming.FitmentIntro, 2000);
        incoming.CriticalRisks = incoming.CriticalRisks.Select(s => Trim(s, 2000)).Where(s => s.Length > 0).Take(30).ToList();
        incoming.TopParts = incoming.TopParts.Where(p => !string.IsNullOrWhiteSpace(p.PartNumber) || p.TotalClaims != 0).Take(10).ToList();
        if (incoming.TopParts.Count > 10) throw new AppException("Top returning parts holds up to 10 rows.");
        incoming.VehicleModels = incoming.VehicleModels.Where(v => !string.IsNullOrWhiteSpace(v.Name)).Take(20).ToList();
        incoming.RootCause = incoming.RootCause.Where(n => !string.IsNullOrWhiteSpace(n.Label) || !string.IsNullOrWhiteSpace(n.Text)).Take(20).ToList();
        incoming.Recommendations = incoming.Recommendations.Where(n => !string.IsNullOrWhiteSpace(n.Label) || !string.IsNullOrWhiteSpace(n.Text)).Take(20).ToList();
        incoming.FirstArticle = incoming.FirstArticle.Where(r => !string.IsNullOrWhiteSpace(r.Category) && !r.Category.Trim().Equals("Total", StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
        incoming.Ncrs = incoming.Ncrs.Where(n => !string.IsNullOrWhiteSpace(n.Number) || !string.IsNullOrWhiteSpace(n.Defect)).Take(50).ToList();
        incoming.Fitment = incoming.Fitment.Where(p => !string.IsNullOrWhiteSpace(p.Problem) || !string.IsNullOrWhiteSpace(p.Response)).Take(30).ToList();
        incoming.ProductInfo = incoming.ProductInfo.Where(p => !string.IsNullOrWhiteSpace(p.Problem) || !string.IsNullOrWhiteSpace(p.Response)).Take(30).ToList();
        return incoming;
    }

    static string Trim(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    static void CheckMonth(int year, int month)
    {
        if (month is < 1 or > 12 || year is < 2000 or > 2100) throw new AppException("Choose a month and a year.");
    }

    static (int Year, int Month) Previous(int year, int month) => month == 1 ? (year - 1, 12) : (year, month - 1);

    static void EnsureDraft(MonthlyQualityReport report)
    {
        if (report.Status == "Final") throw new AppException("This report is final. Reopen it before changing it.");
    }

    async Task RequireAsync(string permission, CancellationToken ct)
    {
        if (!await m.CanAnyAsync(permission, ct)) throw new ForbiddenException();
    }

    async Task<MonthlyQualityReport> LoadAsync(Guid id, CancellationToken ct) =>
        await db.MonthlyQualityReports.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Monthly report");

    static MonthlyReportBody Read(MonthlyQualityReport report) => SequflohJson.Read<MonthlyReportBody>(report.Body);
    static string Write(MonthlyReportBody body) => SequflohJson.Write(body);

    sealed class ImageSlot(string type, string label, string[] extensions) : IMonthlySourceParser
    {
        public string Type => type;
        public string Label => label;
        public string Hint => "Replaces the generated picture.";
        public bool Ready => true;
        public IReadOnlyList<string> Extensions => extensions;
        public IReadOnlyList<string> Fills => [];
        public SourceImport Read(Stream content, int year, int month) => new();
    }

    sealed record Wo(Guid Id, TrackingType Type, TrackingStatus Status, Guid? PartId, string? PartNumber, string? PartDescription, string? FamilyName, string? InspectionSubType,
        string Result, string? Deviations, bool Quarantine, string? QuarantineReason, string? NonconformanceReferences, string? ObservedCondition, string? FailureReason,
        string? InspectionDescription, string? FinalDisposition, DateTimeOffset? ActualCompletion, DateTimeOffset CreatedAt, bool UseCrewTime);
}
