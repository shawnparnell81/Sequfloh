using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Tracking;

namespace Sequfloh.Infrastructure.Modules.Execution;

public sealed record ChargebackPart(string PartNumber, string? Description, decimal Quantity, decimal? UnitCost);
public sealed record ChargebackInput(string WorkOrderNumber, Guid? TrackingRecordId, string? Supplier, string? SupplierReference, Guid? PartId, string? PartNumber, string? PartDescription,
    string? TaskDescription, string? Result, int? QtyInspected, int? QtyBad, int? TotalPersonnel, decimal? TotalHours, DateOnly? DateFrom, DateOnly? DateTo, decimal? HourlyRate,
    string? DiscoveryMethod, string? SupplierPartNumber, string? LotDateCodes, string? RootCause, string? CorrectiveAction, List<ChargebackPart>? Parts, List<CostLine>? CostLines, string? Notes);

/// <summary>
/// Saved supplier chargeback reports, laid out like the work order chargeback job report: chargeback, job, labor (crew and registered people
/// separately, then combined), parts replaced, findings, chargeback summary and sign-off. The work order number is typed by the user. No inventory is kept here.
/// </summary>
public sealed class SequflohChargebackService(AppDbContext db, ModuleSupport m, ICurrentUser current, SequflohTrackingService tracking, SequflohCrewService crew,
    SequflohFoundationService f, SequflohPrintService print)
{
    const string Entity = nameof(SequflohChargebackReport);
    static List<ChargebackPart> PartsOf(string json) => JsonSerializer.Deserialize<List<ChargebackPart>>(json, SequflohJson.Options) ?? [];
    static List<CostLine> LinesOf(string json) => JsonSerializer.Deserialize<List<CostLine>>(json, SequflohJson.Options) ?? [];
    static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, max)];
    static string Money(decimal d) => d.ToString("#,##0.00", CultureInfo.InvariantCulture);
    static string N(decimal d) => d.ToString("#,##0.##", CultureInfo.InvariantCulture);

    async Task RequireViewAsync(CancellationToken ct) { if (!await m.CanAnyAsync(Permissions.SequflohViewReports, ct)) throw new ForbiddenException(); }
    async Task RequireEditAsync(CancellationToken ct) { if (!await m.CanAnyAsync(Permissions.SequflohManageChargebacks, ct)) throw new ForbiddenException(); }

    public static decimal? LaborCost(decimal? hours, decimal? rate) => hours is { } h && rate is { } r ? Math.Round(h * r, 2) : null;
    public static decimal PartsCost(IEnumerable<ChargebackPart> parts) => parts.Sum(p => p.UnitCost is { } u ? Math.Round(u * p.Quantity, 2) : 0);

    /// <summary>What a linked work order fills in: hours (crew and registered people), personnel, dates, part and quantities.</summary>
    public async Task<object> PrefillAsync(Guid trackingId, CancellationToken ct)
    {
        var r = await tracking.LoadAsync(trackingId, false, ct);
        var t = await crew.TotalsAsync(trackingId, ct);
        var fd = SequflohJobService.FindingsOf(r.Findings);
        return new
        {
            TrackingRecordId = r.Id, TrackingNumber = r.Number, r.Type, WorkOrderNumber = r.WorkOrder, r.PartId, r.PartNumber, r.PartDescription, Result = r.Result == "Pending" ? null : r.Result,
            QtyInspected = fd.QtyInspected ?? (r.QuantityCompleted > 0 ? r.QuantityCompleted : r.QuantityTarget), QtyBad = fd.QtyNonconforming ?? (r.QuantityNotRepaired > 0 ? r.QuantityNotRepaired : (int?)null),
            fd.Supplier, SupplierReference = fd.PurchaseOrder, fd.DiscoveryMethod, fd.SupplierPartNumber, fd.LotDateCodes, fd.RootCause, TaskDescription = fd.Description ?? r.WorkInstructionTitle,
            CrewHours = Hr(t.CrewLaborSeconds), UserHours = Hr(t.UserSeconds), TotalHours = Hr(t.TotalSeconds), TotalPersonnel = t.Personnel, CrewSize = t.MaxCrew, t.Users,
            DateFrom = t.From, DateTo = t.To,
        };
    }
    static decimal Hr(long s) => Math.Round(s / 3600m, 2);

    public async Task<object> ListAsync(string? q, CancellationToken ct)
    {
        await RequireViewAsync(ct);
        var query = db.SequflohChargebackReports.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.WorkOrderNumber, like) || (x.PartNumber != null && EF.Functions.ILike(x.PartNumber, like)) || (x.Supplier != null && EF.Functions.ILike(x.Supplier, like)));
        }
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(x => new
        {
            x.Id, x.WorkOrderNumber, x.TrackingNumber, x.Supplier, x.PartNumber, x.Result, x.QtyInspected, x.QtyBad, x.TotalPersonnel, x.TotalHours, x.DateFrom, x.DateTo,
            Total = Total(x), x.CreatedAt, x.CreatedByName,
        });
    }

    static decimal? Total(SequflohChargebackReport x)
    {
        var labor = LaborCost(x.TotalHours, x.HourlyRate);
        var other = PartsCost(PartsOf(x.Parts)) + LinesOf(x.CostLines).Sum(l => l.Amount);
        return labor is null && other == 0 ? null : (labor ?? 0) + other;
    }

    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        await RequireViewAsync(ct);
        var x = await db.SequflohChargebackReports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Chargeback report");
        return Dto(x);
    }

    static object Dto(SequflohChargebackReport x) => new
    {
        x.Id, x.WorkOrderNumber, x.TrackingRecordId, x.TrackingNumber, x.Supplier, x.SupplierReference, x.PartId, x.PartNumber, x.PartDescription, x.TaskDescription, x.Result,
        x.QtyInspected, x.QtyBad, x.TotalPersonnel, x.TotalHours, x.CrewHours, x.UserHours, x.DateFrom, x.DateTo, x.HourlyRate, LaborCost = LaborCost(x.TotalHours, x.HourlyRate),
        x.DiscoveryMethod, x.SupplierPartNumber, x.LotDateCodes, x.RootCause, x.CorrectiveAction, Parts = PartsOf(x.Parts), CostLines = LinesOf(x.CostLines), x.Notes,
        PartsCost = PartsCost(PartsOf(x.Parts)), OtherCost = LinesOf(x.CostLines).Sum(l => l.Amount), Total = Total(x), x.CreatedAt, x.CreatedByName,
    };

    string Snapshot(SequflohChargebackReport x) => JsonSerializer.Serialize(Dto(x), SequflohJson.Options);

    async Task FillAsync(SequflohChargebackReport x, ChargebackInput i, CancellationToken ct)
    {
        x.WorkOrderNumber = T(i.WorkOrderNumber, 100) ?? throw new AppException("Enter the work order number.");
        if (i.QtyInspected is < 0 || i.QtyBad is < 0 || i.TotalPersonnel is < 0 || i.TotalHours is < 0 || i.HourlyRate is < 0) throw new AppException("Numbers can't be negative.");
        if (i.QtyBad is { } b && i.QtyInspected is { } q && b > q) throw new AppException("Qty bad can't be more than the quantity inspected / reworked.");
        if (i.DateFrom is { } df && i.DateTo is { } dt && dt < df) throw new AppException("The end date must be on or after the start date.");
        x.TrackingRecordId = null; x.TrackingNumber = null; x.CrewHours = null; x.UserHours = null; x.SiteId = null;
        if (i.TrackingRecordId is { } tid)
        {
            var r = await tracking.LoadAsync(tid, false, ct);
            await m.RequireAsync(Permissions.SequflohManageChargebacks, r.SiteId, r.DepartmentId, ct);
            var t = await crew.TotalsAsync(tid, ct);
            x.TrackingRecordId = r.Id; x.TrackingNumber = r.Number; x.SiteId = r.SiteId; x.CrewHours = Hr(t.CrewLaborSeconds); x.UserHours = Hr(t.UserSeconds);
        }
        if (T(i.Result, 100) is { } res) await f.RequireOptionAsync("validation-results", res, "Result", ct);
        if (T(i.DiscoveryMethod, 100) is { } dm) await f.RequireOptionAsync("discovery-methods", dm, "Discovery method", ct);
        x.Supplier = T(i.Supplier, 200); x.SupplierReference = T(i.SupplierReference, 100); x.PartId = i.PartId; x.PartNumber = T(i.PartNumber, 100); x.PartDescription = T(i.PartDescription, 200);
        x.TaskDescription = T(i.TaskDescription, 4000); x.Result = T(i.Result, 100); x.QtyInspected = i.QtyInspected; x.QtyBad = i.QtyBad; x.TotalPersonnel = i.TotalPersonnel;
        x.TotalHours = i.TotalHours is { } th ? Math.Round(th, 2) : (x.CrewHours is not null ? x.CrewHours + x.UserHours : null);
        x.DateFrom = i.DateFrom; x.DateTo = i.DateTo; x.HourlyRate = i.HourlyRate is { } hr ? Math.Round(hr, 2) : null;
        x.DiscoveryMethod = T(i.DiscoveryMethod, 100); x.SupplierPartNumber = T(i.SupplierPartNumber, 100); x.LotDateCodes = T(i.LotDateCodes, 300);
        x.RootCause = T(i.RootCause, 4000); x.CorrectiveAction = T(i.CorrectiveAction, 4000); x.Notes = T(i.Notes, 4000);
        var parts = (i.Parts ?? []).Where(p => !string.IsNullOrWhiteSpace(p.PartNumber)).Select(p =>
        {
            if (p.Quantity < 0 || p.UnitCost < 0) throw new AppException($"Quantities and costs for {p.PartNumber} can't be negative.");
            return new ChargebackPart(p.PartNumber.Trim()[..Math.Min(p.PartNumber.Trim().Length, 100)], T(p.Description, 200), p.Quantity, p.UnitCost);
        }).ToList();
        var lines = (i.CostLines ?? []).Where(l => !string.IsNullOrWhiteSpace(l.Description)).Select(l => new CostLine(l.Description.Trim()[..Math.Min(l.Description.Trim().Length, 200)], Math.Round(l.Amount, 2))).ToList();
        if (parts.Count > 100 || lines.Count > 50) throw new AppException("Up to 100 parts and 50 cost lines.");
        x.Parts = JsonSerializer.Serialize(parts, SequflohJson.Options); x.CostLines = JsonSerializer.Serialize(lines, SequflohJson.Options);
    }

    public async Task<object> CreateAsync(ChargebackInput i, CancellationToken ct)
    {
        await RequireEditAsync(ct);
        var x = new SequflohChargebackReport { CreatedByName = await db.Users.Where(u => u.Id == m.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? current.UserName ?? "" };
        await FillAsync(x, i, ct);
        db.SequflohChargebackReports.Add(x);
        f.Audit("ChargebackReportCreated", Entity, x.Id, $"Chargeback report for work order {x.WorkOrderNumber} created", x.SiteId, context: new { before = (string?)null, after = Snapshot(x) });
        await db.SaveChangesAsync(ct);
        return Dto(x);
    }

    public async Task<object> UpdateAsync(Guid id, ChargebackInput i, CancellationToken ct)
    {
        await RequireEditAsync(ct);
        var x = await db.SequflohChargebackReports.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Chargeback report");
        var before = Snapshot(x);
        await FillAsync(x, i, ct);
        f.Audit("ChargebackReportUpdated", Entity, x.Id, $"Chargeback report for work order {x.WorkOrderNumber} updated", x.SiteId, context: new { before, after = Snapshot(x) });
        await db.SaveChangesAsync(ct);
        return Dto(x);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await RequireEditAsync(ct);
        var x = await db.SequflohChargebackReports.FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Chargeback report");
        f.Audit("ChargebackReportDeleted", Entity, x.Id, $"Chargeback report for work order {x.WorkOrderNumber} deleted", x.SiteId, context: new { before = Snapshot(x), after = (string?)null });
        db.SequflohChargebackReports.Remove(x);
        await db.SaveChangesAsync(ct);
    }

    async Task<SequflohChargebackReport> LoadAsync(Guid id, CancellationToken ct)
    {
        await RequireViewAsync(ct);
        return await db.SequflohChargebackReports.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw new NotFoundException("Chargeback report");
    }

    /// <summary>The report as sections of label / value rows, shared by the PDF and the Excel file so both read the same.</summary>
    async Task<List<(string Heading, List<(string Label, string? Value)> Fields)>> SectionsAsync(SequflohChargebackReport x, Func<DateTimeOffset?, string> local, CancellationToken ct)
    {
        string D(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "";
        string? I(int? v) => v?.ToString("#,##0", CultureInfo.InvariantCulture);
        string? Hd(decimal? v) => v is { } h ? h.ToString("#,##0.00", CultureInfo.InvariantCulture) : null;
        var good = x.QtyInspected is { } qi && x.QtyBad is { } qb ? qi - qb : (int?)null;
        var rate = x.QtyInspected is > 0 && x.QtyBad is { } bad ? (bad * 100m / x.QtyInspected.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%" : null;
        var org = await print.OrganizationAsync(ct);
        var perPart = x.TotalHours is { } th && x.QtyInspected is > 0 ? (th * 60m / x.QtyInspected.Value).ToString("0.00", CultureInfo.InvariantCulture) + " min" : null;
        return
        [
            ("Chargeback", [("Supplier", x.Supplier ?? "____"), ("Our company", org), ("Supplier reference / PO", x.SupplierReference ?? "____"), ("Report date", local(x.CreatedAt))]),
            ("Job", [("Work order #", x.WorkOrderNumber), ("Linked work order", x.TrackingNumber), ("Part number", x.PartNumber ?? "—"), ("Description", x.PartDescription),
                ("Supplier part number", x.SupplierPartNumber), ("Lot / date codes", x.LotDateCodes), ("Dates", x.DateFrom is null && x.DateTo is null ? null : $"{D(x.DateFrom)} to {D(x.DateTo)}")]),
            ("Task", [("Description of task", x.TaskDescription), ("Discovery method", x.DiscoveryMethod), ("Result", x.Result)]),
            ("Results", [("Qty inspected / reworked", I(x.QtyInspected)), ("Qty bad", I(x.QtyBad)), ("Qty good", I(good)), ("Bad rate", rate)]),
            ("Labor", [("Total personnel on the task", I(x.TotalPersonnel)), ("Crew labor hours", Hd(x.CrewHours)), ("Registered people hours", Hd(x.UserHours)),
                ("Total hours worked", Hd(x.TotalHours) is { } tt ? tt + " (paused time excluded)" : null), ("Minutes per part", perPart)]),
            ("Findings", [("Supplier root cause", x.RootCause), ("Corrective action requested", x.CorrectiveAction), ("Notes", x.Notes)]),
        ];
    }

    public async Task<(byte[] Pdf, string Name)> PdfAsync(Guid id, CancellationToken ct)
    {
        var x = await LoadAsync(id, ct);
        var (printed, local) = await print.ClockAsync(x.SiteId, ct);
        var blocks = new List<PrintBlock>();
        foreach (var (h, fields) in await SectionsAsync(x, local, ct))
        {
            if (fields.All(v => string.IsNullOrWhiteSpace(v.Value))) continue;
            blocks.Add(new PrintHeading(h));
            blocks.Add(new PrintFields(fields));
        }
        var parts = PartsOf(x.Parts);
        if (parts.Count > 0)
        {
            blocks.Add(new PrintHeading("Parts replaced"));
            blocks.Add(new PrintTable([("Part number", 2.5), ("Description", 3), ("Quantity", 1.2), ("Unit cost", 1.2), ("Amount", 1.3)],
                [.. parts.Select(p => new string?[] { p.PartNumber, p.Description ?? "", N(p.Quantity), p.UnitCost is { } u ? Money(u) : "", p.UnitCost is { } u2 ? Money(Math.Round(u2 * p.Quantity, 2)) : "" })]));
        }
        if (x.TrackingRecordId is { } tid)
        {
            var now = m.Now;
            var sessions = await db.SequflohCrewSessions.AsNoTracking().Where(s => s.TrackingRecordId == tid).OrderBy(s => s.StartedAt).ToListAsync(ct);
            if (sessions.Count > 0)
            {
                blocks.Add(new PrintHeading("Crew time"));
                blocks.Add(new PrintTable([("Start", 2.2), ("Stop", 2.2), ("Crew", 0.8), ("Worked h", 1), ("Paused h", 1), ("Labor h", 1)],
                    [.. sessions.Select(s => new string?[] { local(s.StartedAt), s.StoppedAt is { } st ? local(st) : "running", s.CrewSize.ToString(CultureInfo.InvariantCulture),
                        SequflohCrewService.H(SequflohCrewService.Worked(s, now)), SequflohCrewService.H(SequflohCrewService.Paused(s, now)), SequflohCrewService.H(SequflohCrewService.Labor(s, now)) }),
                     ["Total", "", "", SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Worked(s, now))), SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Paused(s, now))), SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Labor(s, now)))]]));
            }
            var entries = await db.SequflohTimeEntries.AsNoTracking().Where(e => e.TrackingRecordId == tid).ToListAsync(ct);
            if (entries.Count > 0)
            {
                blocks.Add(new PrintHeading("Man hours by registered person"));
                var people = entries.GroupBy(e => e.UserId).Select(g => (Name: g.OrderBy(e => e.StartedAt).Last().UserName, Sessions: g.Count(),
                    Worked: g.Sum(e => SequflohTimeService.Worked(e, now)), Paused: g.Sum(e => SequflohTimeService.Paused(e, now)))).OrderBy(p => p.Name).ToList();
                blocks.Add(new PrintTable([("Person", 3), ("Sessions", 1), ("Worked hours", 1.3), ("Paused hours (not counted)", 1.8)],
                    [.. people.Select(p => new string?[] { p.Name, p.Sessions.ToString(CultureInfo.InvariantCulture), SequflohJobService.Hours(p.Worked), SequflohJobService.Hours(p.Paused) }),
                     ["Total", people.Sum(p => p.Sessions).ToString(CultureInfo.InvariantCulture), SequflohJobService.Hours(people.Sum(p => p.Worked)), SequflohJobService.Hours(people.Sum(p => p.Paused))]]));
            }
        }
        blocks.Add(new PrintHeading("Chargeback summary"));
        var labor = LaborCost(x.TotalHours, x.HourlyRate);
        var rows = new List<string?[]> { new[] { "Labor", $"{(x.TotalHours is { } hh ? hh.ToString("0.00", CultureInfo.InvariantCulture) : "____")} h × {(x.HourlyRate is { } r ? Money(r) : "____")}", labor is { } l ? Money(l) : "____" } };
        rows.AddRange(parts.Where(p => p.UnitCost is not null).Select(p => new string?[] { $"Part {p.PartNumber}", $"{N(p.Quantity)} × {Money(p.UnitCost!.Value)}", Money(Math.Round(p.UnitCost!.Value * p.Quantity, 2)) }));
        rows.AddRange(LinesOf(x.CostLines).Select(c => new string?[] { c.Description, "", Money(c.Amount) }));
        blocks.Add(new PrintTable([("Item", 3), ("Basis", 2.5), ("Amount", 1.5)], rows));
        var total = Total(x);
        blocks.Add(new PrintFields([("Total chargeback", total is { } t ? Money(t) + (labor is null ? " (labor not included: no rate entered)" : "") : "____")]));
        blocks.Add(new PrintHeading("Sign-off"));
        blocks.Add(new PrintFields([("Prepared by", "________________"), ("Date", "________"), ("Supplier acknowledgement", "________________")]));
        var org = await print.OrganizationAsync(ct);
        var header = new PrintHeader(org, $"Chargeback Report: {x.PartNumber ?? x.WorkOrderNumber}", x.WorkOrderNumber, null, x.Result ?? "", null, x.Supplier is null ? null : $"Supplier: {x.Supplier}",
            "Chargeback report", current.UserName ?? "", printed, null);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("ChargebackReportPrinted", Entity, x.Id, $"Printed the chargeback report for work order {x.WorkOrderNumber}", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"chargeback-{Safe(x.WorkOrderNumber)}.pdf");
    }

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    public async Task<(byte[] Bytes, string Name)> ExcelAsync(Guid id, CancellationToken ct)
    {
        var x = await LoadAsync(id, ct);
        var (_, local) = await print.ClockAsync(x.SiteId, ct);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Chargeback");
        var row = 1;
        ws.Cell(row, 1).Value = $"Chargeback Report: {x.WorkOrderNumber}"; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 1).Style.Font.FontSize = 14; row += 2;
        foreach (var (h, fields) in await SectionsAsync(x, local, ct))
        {
            ws.Cell(row, 1).Value = h; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF5"); ws.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF5"); row++;
            foreach (var (l, v) in fields) { ws.Cell(row, 1).Value = l; ws.Cell(row, 2).Value = v ?? ""; row++; }
            row++;
        }
        var parts = PartsOf(x.Parts);
        if (parts.Count > 0)
        {
            ws.Cell(row, 1).Value = "Parts replaced"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
            foreach (var p in parts) { ws.Cell(row, 1).Value = p.PartNumber; ws.Cell(row, 2).Value = p.Description ?? ""; ws.Cell(row, 3).Value = p.Quantity; if (p.UnitCost is { } u) { ws.Cell(row, 4).Value = u; ws.Cell(row, 5).Value = Math.Round(u * p.Quantity, 2); } row++; }
            row++;
        }
        ws.Cell(row, 1).Value = "Chargeback summary"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
        ws.Cell(row, 1).Value = "Hourly rate"; if (x.HourlyRate is { } hr) ws.Cell(row, 2).Value = hr; row++;
        ws.Cell(row, 1).Value = "Labor"; if (LaborCost(x.TotalHours, x.HourlyRate) is { } lc) ws.Cell(row, 2).Value = lc; row++;
        ws.Cell(row, 1).Value = "Parts"; ws.Cell(row, 2).Value = PartsCost(parts); row++;
        foreach (var c in LinesOf(x.CostLines)) { ws.Cell(row, 1).Value = c.Description; ws.Cell(row, 2).Value = c.Amount; row++; }
        ws.Cell(row, 1).Value = "Total chargeback"; ws.Cell(row, 1).Style.Font.Bold = true; if (Total(x) is { } t) ws.Cell(row, 2).Value = t; ws.Cell(row, 2).Style.Font.Bold = true;
        ws.Column(1).Width = 32; ws.Column(2).Width = 60; ws.Column(2).Style.Alignment.WrapText = true; ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        f.Audit("ChargebackReportExported", Entity, x.Id, $"Exported the chargeback report for work order {x.WorkOrderNumber} to Excel", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (ms.ToArray(), $"chargeback-{Safe(x.WorkOrderNumber)}.xlsx");
    }
}
