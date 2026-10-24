using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;
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
    string? DiscoveryMethod, string? SupplierPartNumber, string? LotDateCodes, string? RootCause, string? CorrectiveAction, List<ChargebackPart>? Parts, List<CostLine>? CostLines, string? Notes, List<string>? Sections = null);

/// <summary>
/// Saved supplier chargeback reports, laid out like the work order chargeback job report: chargeback, job, labor (crew and registered people
/// separately, then combined), parts replaced, findings, chargeback summary and sign-off. The work order number is typed by the user. No inventory is kept here.
/// </summary>
public sealed class SequflohChargebackService(AppDbContext db, ModuleSupport m, ICurrentUser current, SequflohTrackingService tracking, SequflohCrewService crew,
    SequflohFoundationService f, SequflohPrintService print, SequflohJobService jobs, IFileStorage storage)
{
    const string Entity = nameof(SequflohChargebackReport);

    public sealed record SectionOption(string Key, string Label, bool Default);
    /// <summary>What can be ticked to appear in the report, in report order. Sections with nothing to show are left out when printing.</summary>
    public static readonly SectionOption[] SectionOptions =
    [
        new("header", "Chargeback details (supplier, our company, PO, report date)", true),
        new("job", "Job (work order, record, job type, work instruction, dates)", true),
        new("part", "Part details (part number, description)", true),
        new("task", "Task description", true),
        new("results", "Result and quantities", true),
        new("labor", "Total personnel and total man hours", true),
        new("parts", "Parts replaced", true),
        new("findings", "Findings / nonconformance", true),
        new("rootCause", "Supplier root cause", true),
        new("correctiveAction", "Corrective action requested", true),
        new("crewSummary", "Worker time summary (crew and registered people)", true),
        new("crewSessions", "Crew time sessions", true),
        new("userTime", "Man hours by technician (registered people)", true),
        new("timeLog", "Detailed time log (registered people)", false),
        new("attachments", "Attachments (from the linked work order)", false),
        new("photos", "Photos and evidence (from the linked work order)", false),
        new("notes", "Notes", true),
        new("cost", "Chargeback summary (rate and costs)", true),
        new("signoff", "Sign-off and signatures", true),
    ];
    public static List<string> SectionsOf(string? json)
    {
        var saved = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<string>>(json, SequflohJson.Options);
        return saved ?? [.. SectionOptions.Where(o => o.Default).Select(o => o.Key)];
    }
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
        Sections = SectionsOf(x.Sections), PartsCost = PartsCost(PartsOf(x.Parts)), OtherCost = LinesOf(x.CostLines).Sum(l => l.Amount), Total = Total(x), x.CreatedAt, x.CreatedByName,
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
        if (i.Sections is { } sec)
        {
            var known = SectionOptions.Select(o => o.Key).ToHashSet();
            if (sec.FirstOrDefault(k => !known.Contains(k)) is { } bad) throw new AppException($"Unknown report section \"{bad}\".");
            x.Sections = JsonSerializer.Serialize(SectionOptions.Select(o => o.Key).Where(sec.Contains).ToList(), SequflohJson.Options);
        }
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

    /// <summary>One block of the printed report: a heading with label / value rows, a table, photos or a line of text.</summary>
    public sealed record ReportBlock(string Key, string Heading, List<(string Label, string? Value)>? Fields = null, List<(string Name, double Width)>? Columns = null,
        List<string?[]>? Rows = null, List<(Guid Id, string Caption)>? Photos = null);

    static string? I(int? v) => v?.ToString(CultureInfo.InvariantCulture);
    static string? Hd(decimal? v) => v is { } h ? h.ToString("0.00", CultureInfo.InvariantCulture) : null;
    static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "____" : s.Trim();

    /// <summary>
    /// The report in the order and style of the work order chargeback job report (SequflohJobService.PdfAsync), with crew time added.
    /// Only the ticked sections are returned; the PDF, the Excel file and the screen all use this list.
    /// </summary>
    public async Task<(SequflohChargebackReport Report, List<ReportBlock> Blocks, Func<DateTimeOffset?, string> Local, string Printed)> BlocksAsync(Guid id, CancellationToken ct)
    {
        var x = await LoadAsync(id, ct);
        var on = SectionsOf(x.Sections).ToHashSet();
        var (printed, local) = await print.ClockAsync(x.SiteId, ct);
        var org = await print.OrganizationAsync(ct);
        var now = m.Now;
        SequflohJobService.JobReport? job = null;
        List<SequflohCrewSession> sessions = [];
        if (x.TrackingRecordId is { } tid)
        {
            try { job = await jobs.ReportAsync(tid, ct); } catch (ForbiddenException) { } catch (NotFoundException) { }
            sessions = await db.SequflohCrewSessions.AsNoTracking().Where(s => s.TrackingRecordId == tid).OrderBy(s => s.StartedAt).ToListAsync(ct);
        }
        var fd = job?.Findings ?? new JobFindings();
        var parts = PartsOf(x.Parts);
        var lines = LinesOf(x.CostLines);
        var blocks = new List<ReportBlock>();
        void Add(string key, string heading, List<(string, string?)> fields) { if (on.Contains(key) && fields.Any(v => !string.IsNullOrWhiteSpace(v.Item2))) blocks.Add(new(key, heading, fields)); }

        Add("header", "Chargeback", [("Supplier", Blank(x.Supplier)), ("Our company", org), ("Supplier reference / PO", Blank(x.SupplierReference)), ("Report date", local(x.CreatedAt))]);

        // Job: the original job block, with the typed work order #, task, result and quantities in it
        var jobFields = new List<(string, string?)>();
        if (on.Contains("job"))
            jobFields.AddRange([("Work order", x.WorkOrderNumber), ("Record", x.TrackingNumber), ("Job type", job?.Type),
                ("Work instruction", job is null ? null : $"{job.WorkInstructionNumber} rev {job.WorkInstructionRevision} {job.Title}".Trim()),
                ("Dates", x.DateFrom is null && x.DateTo is null ? null : $"{x.DateFrom:yyyy-MM-dd} to {x.DateTo:yyyy-MM-dd}"), ("Status", job?.Status)]);
        if (on.Contains("part")) jobFields.AddRange([("Assembly part number", x.PartNumber ?? "—"), ("Description", x.PartDescription ?? "—")]);
        if (on.Contains("task")) jobFields.Add(("Task description", x.TaskDescription));
        if (on.Contains("results"))
        {
            var good = x.QtyInspected is { } qi && x.QtyBad is { } qb ? qi - qb : (int?)null;
            jobFields.AddRange([("Result", x.Result), ("Quantity inspected / reworked", I(x.QtyInspected)), ("Qty bad", I(x.QtyBad)), ("Qty good", I(good)),
                ("Bad rate", x.QtyInspected is > 0 && x.QtyBad is { } bad ? (bad * 100m / x.QtyInspected.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%" : null)]);
        }
        if (on.Contains("labor"))
            jobFields.AddRange([("Total personnel", I(x.TotalPersonnel)), ("Total man hours", Hd(x.TotalHours) is { } th ? th + " (paused time excluded)" : null)]);
        if (jobFields.Any(v => !string.IsNullOrWhiteSpace(v.Item2))) blocks.Add(new("job", "Job", jobFields));

        if (on.Contains("parts") && parts.Count > 0)
            blocks.Add(new("parts", "Parts replaced", Columns: [("Part number", 2.5), ("Description", 3), ("Quantity replaced", 1.5)],
                Rows: [.. parts.Select(p => new string?[] { p.PartNumber, p.Description ?? "", N(p.Quantity) }), ["Total", "", N(parts.Sum(p => p.Quantity))]]));

        Add("findings", "Findings / nonconformance", [("Discovery method", x.DiscoveryMethod ?? fd.DiscoveryMethod), ("Date discovered", fd.DateDiscovered?.ToString("yyyy-MM-dd")),
            ("Discovered by", fd.DiscoveredBy), ("Site", fd.Site), ("Supplier", x.Supplier ?? fd.Supplier), ("Supplier part number", x.SupplierPartNumber ?? fd.SupplierPartNumber),
            ("Lot / date codes", x.LotDateCodes ?? fd.LotDateCodes), ("PO", x.SupplierReference ?? fd.PurchaseOrder), ("Qty inspected", I(x.QtyInspected ?? fd.QtyInspected)),
            ("Qty nonconforming", I(x.QtyBad ?? fd.QtyNonconforming)), ("Qty affected", I(fd.QtyAffected)), ("Warranty claim #", fd.WarrantyClaim), ("Alert / recall #", fd.AlertRecall),
            ("Description", fd.Description)]);
        var repair = new List<(string, string?)>();
        if (on.Contains("rootCause")) repair.AddRange([("Repair method", fd.RepairMethod), ("Supplier root cause", x.RootCause ?? fd.RootCause)]);
        if (on.Contains("correctiveAction")) repair.Add(("Corrective action requested", x.CorrectiveAction));
        if (repair.Any(v => !string.IsNullOrWhiteSpace(v.Item2))) blocks.Add(new(on.Contains("rootCause") ? "rootCause" : "correctiveAction", "Repair description", repair));

        // worker time: crew (no names) and registered people, then the combined total
        if (on.Contains("crewSummary") && (sessions.Count > 0 || x.CrewHours is not null || x.UserHours is not null))
        {
            var sizes = sessions.Select(s => s.CrewSize).Distinct().Order().ToList();
            var crewLabor = x.CrewHours ?? (sessions.Count > 0 ? Math.Round(sessions.Sum(s => SequflohCrewService.Labor(s, now)) / 3600m, 2) : (decimal?)null);
            blocks.Add(new("crewSummary", "Worker time", [
                ("Crew elapsed hours", sessions.Count == 0 ? null : SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Worked(s, now))) + " (paused time excluded)"),
                ("Crew size", sizes.Count == 0 ? null : sizes.Count == 1 ? I(sizes[0]) : $"{sizes[0]} to {sizes[^1]}"),
                ("Crew labor hours", Hd(crewLabor) is { } cl ? cl + " (elapsed × crew size)" : null),
                ("Registered people hours", Hd(x.UserHours)),
                ("Combined total", crewLabor is null && x.UserHours is null ? null : Hd((crewLabor ?? 0) + (x.UserHours ?? 0))),
                ("Total personnel", I(x.TotalPersonnel)),
            ]));
        }
        if (on.Contains("crewSessions") && sessions.Count > 0)
            blocks.Add(new("crewSessions", "Crew time sessions", Columns: [("Date", 1.4), ("Start", 1.1), ("Stop", 1.1), ("Paused hours", 1.2), ("Crew size", 1), ("Hours", 1), ("Labor hours", 1.2)],
                Rows: [.. sessions.Select(s => new string?[] { local(s.StartedAt)[..10], local(s.StartedAt)[11..16], s.StoppedAt is { } st ? local(st)[11..16] : "running",
                        SequflohCrewService.H(SequflohCrewService.Paused(s, now)), I(s.CrewSize), SequflohCrewService.H(SequflohCrewService.Worked(s, now)), SequflohCrewService.H(SequflohCrewService.Labor(s, now)) }),
                    ["Total", "", "", SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Paused(s, now))), "", SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Worked(s, now))),
                        SequflohCrewService.H(sessions.Sum(s => SequflohCrewService.Labor(s, now)))]]));
        if (on.Contains("userTime") && job is { People.Count: > 0 })
            blocks.Add(new("userTime", "Man hours by technician", Columns: [("Technician", 3), ("Sessions", 1), ("Worked hours", 1.3), ("Paused hours (not counted)", 1.8)],
                Rows: [.. job.People.Select(p => new string?[] { p.Name, I(p.Sessions), SequflohJobService.Hours(p.Seconds), SequflohJobService.Hours(p.PausedSeconds) }),
                    ["Total", I(job.People.Sum(p => p.Sessions)), SequflohJobService.Hours(job.TotalSeconds), SequflohJobService.Hours(job.PausedSeconds)]]));
        if (on.Contains("timeLog") && job?.TimeLog is { Count: > 0 } tl)
            blocks.Add(new("timeLog", "Detailed time log", Columns: [("When", 2.2), ("Technician", 2.5), ("Event", 1.6)], Rows: [.. tl.Select(e => new string?[] { local(e.At), e.Name, e.Kind })]));
        if (on.Contains("attachments") && job?.Attachments is { Count: > 0 } at)
            blocks.Add(new("attachments", "Attachments", Columns: [("File", 4), ("Added by", 2), ("Added", 2)], Rows: [.. at.Select(a => new string?[] { a.FileName, a.UploadedByName, local(a.UploadedAt) })]));
        if (on.Contains("photos") && job?.Photos is { Count: > 0 } ph)
            blocks.Add(new("photos", "Photos and evidence", Photos: [.. ph.Select(p => (p.Id, $"{p.FileName} · {p.UploadedByName} · {local(p.UploadedAt)}"))]));
        Add("notes", "Notes", [("Notes", x.Notes)]);
        if (on.Contains("cost"))
        {
            var labor = LaborCost(x.TotalHours, x.HourlyRate);
            var rows = new List<string?[]> { new[] { "Labor", $"{Hd(x.TotalHours) ?? "____"} h × {(x.HourlyRate is { } r ? Money(r) : "____")}", labor is { } l ? Money(l) : "____" } };
            rows.AddRange(parts.Where(p => p.UnitCost is not null).Select(p => new string?[] { $"Part {p.PartNumber}", $"{N(p.Quantity)} × {Money(p.UnitCost!.Value)}", Money(Math.Round(p.UnitCost!.Value * p.Quantity, 2)) }));
            rows.AddRange(lines.Select(c => new string?[] { c.Description, "", Money(c.Amount) }));
            blocks.Add(new("cost", "Chargeback summary", Columns: [("Item", 3), ("Basis", 2.5), ("Amount", 1.5)], Rows: rows));
            var pc = PartsCost(parts); var oc = lines.Sum(l => l.Amount); var total = (labor ?? 0) + pc + oc;
            blocks.Add(new("cost", "", [("Labor", labor is { } l2 ? Money(l2) : "____ (computed when a rate is entered)"), ("Parts", pc == 0 ? null : Money(pc)), ("Other costs", oc == 0 ? null : Money(oc)),
                ("Total chargeback", labor is null && total == 0 ? "____" : Money(total) + (labor is null ? " (labor not included: no rate entered)" : ""))]));
        }
        if (on.Contains("signoff")) blocks.Add(new("signoff", "Sign-off", [("Prepared by", "________________"), ("Date", "________"), ("Supplier acknowledgement", "________________")]));
        return (x, blocks, local, printed);
    }

    /// <summary>The report blocks for the screen (photos as captions only).</summary>
    public async Task<object> ViewAsync(Guid id, CancellationToken ct)
    {
        var (_, blocks, _, _) = await BlocksAsync(id, ct);
        return blocks.Select(b => new
        {
            b.Key, b.Heading, Fields = b.Fields?.Where(v => !string.IsNullOrWhiteSpace(v.Value)).Select(v => new { v.Label, v.Value }),
            Columns = b.Columns?.Select(c => c.Name), b.Rows, Photos = b.Photos?.Select(p => p.Caption),
        });
    }

    public async Task<(byte[] Pdf, string Name)> PdfAsync(Guid id, CancellationToken ct)
    {
        var (x, list, _, printed) = await BlocksAsync(id, ct);
        var blocks = new List<PrintBlock>();
        foreach (var b in list)
        {
            if (b.Heading.Length > 0) blocks.Add(new PrintHeading(b.Heading));
            if (b.Fields is { } fl) blocks.Add(new PrintFields(fl));
            if (b.Columns is { } cols) blocks.Add(new PrintTable(cols, b.Rows ?? []));
            foreach (var (pid, caption) in b.Photos ?? [])
            {
                var file = await db.SequflohTestFiles.AsNoTracking().FirstOrDefaultAsync(e => e.Id == pid, ct);
                byte[]? bytes = null;
                if (file is not null)
                {
                    await using var st = await storage.GetAsync(file.StorageKey, ct);
                    if (st is not null) { using var ms = new MemoryStream(); await st.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
                }
                if (bytes is not null && PdfCanvas.JpegSize(bytes) is not null) blocks.Add(new PrintImage(bytes, caption));
                else blocks.Add(new PrintParagraph($"Photo attached separately: {caption}"));
            }
        }
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
        var (x, list, _, _) = await BlocksAsync(id, ct);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Chargeback");
        var row = 1;
        ws.Cell(row, 1).Value = $"Chargeback Report: {x.PartNumber ?? x.WorkOrderNumber}"; ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 1).Style.Font.FontSize = 14; row++;
        ws.Cell(row, 1).Value = $"Work order {x.WorkOrderNumber}"; row += 2;
        var fill = XLColor.FromHtml("#E8EEF5");
        foreach (var b in list)
        {
            var width = Math.Max(2, b.Columns?.Count ?? 2);
            if (b.Heading.Length > 0) { ws.Cell(row, 1).Value = b.Heading; ws.Range(row, 1, row, width).Style.Fill.BackgroundColor = fill; ws.Cell(row, 1).Style.Font.Bold = true; row++; }
            foreach (var (l, v) in b.Fields ?? []) { if (string.IsNullOrWhiteSpace(v)) continue; ws.Cell(row, 1).Value = l; ws.Cell(row, 2).Value = v; row++; }
            if (b.Columns is { } cols)
            {
                for (var c = 0; c < cols.Count; c++) { ws.Cell(row, c + 1).Value = cols[c].Name; ws.Cell(row, c + 1).Style.Font.Bold = true; }
                row++;
                foreach (var r in b.Rows ?? []) { for (var c = 0; c < r.Length; c++) ws.Cell(row, c + 1).Value = r[c] ?? ""; row++; }
            }
            foreach (var (_, caption) in b.Photos ?? []) { ws.Cell(row, 1).Value = "Photo"; ws.Cell(row, 2).Value = caption; row++; }
            row++;
        }
        ws.Column(1).Width = 30; ws.Column(2).Width = 50; for (var c = 3; c <= 7; c++) ws.Column(c).Width = 16;
        ws.Column(2).Style.Alignment.WrapText = true; ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        f.Audit("ChargebackReportExported", Entity, x.Id, $"Exported the chargeback report for work order {x.WorkOrderNumber} to Excel", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (ms.ToArray(), $"chargeback-{Safe(x.WorkOrderNumber)}.xlsx");
    }
}
