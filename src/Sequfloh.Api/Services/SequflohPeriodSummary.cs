using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>The period a report covers (Day, Week, Month or Custom) with optional filters by person, work order type, inspection sub-type and product family.</summary>
public sealed record ReportPeriod(string Kind, DateOnly? From, DateOnly? To, string? Person = null, string? Type = null, string? SubType = null, Guid? FamilyId = null, Guid? SiteId = null)
{
    public static readonly string[] Kinds = ["Day", "Week", "Month", "Custom"];

    /// <summary>The first and last day covered, both included. A week runs Monday to Sunday.</summary>
    public (DateOnly Start, DateOnly End) Range()
    {
        var d = From ?? throw new AppException("Choose the date the report covers.");
        switch (Kind)
        {
            case "Day": return (d, d);
            case "Week": { var start = d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); return (start, start.AddDays(6)); }
            case "Month": { var start = new DateOnly(d.Year, d.Month, 1); return (start, start.AddMonths(1).AddDays(-1)); }
            case "Custom":
                var e = To ?? throw new AppException("Choose the end date of the custom range.");
                if (e < d) throw new AppException("The end date must be on or after the start date.");
                if (e.DayNumber - d.DayNumber > 366) throw new AppException("A custom range can cover up to one year.");
                return (d, e);
            default: throw new AppException("Choose Single day, Week, Month or Custom range.");
        }
    }
    public string Label() { var (s, e) = Range(); return s == e ? s.ToString("yyyy-MM-dd") : $"{s:yyyy-MM-dd} to {e:yyyy-MM-dd}"; }
}

/// <summary>
/// Period sections for the chargeback report, built only from recorded data: clocked time of signed-in people, crew and named day worker sessions,
/// work order status and dates, pause and hold events with their reasons, inspection results and saved chargeback reports.
/// Time is counted on the day a session started.
/// </summary>
public static class SequflohPeriodSummary
{
    public static readonly string[] Keys = ["sumHours", "sumByType", "sumBySubType", "sumByFamily", "sumByPerson", "sumCounts", "sumQuality", "sumChargebacks", "sumPauses", "sumDetail", "sumCharts"];

    static string H(long s) => (s / 3600m).ToString("0.00", CultureInfo.InvariantCulture);
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    static string Bar(long part, long max) => max <= 0 ? "" : new string('█', (int)Math.Round(20.0 * part / max));
    static readonly Dictionary<TrackingType, string> TypeNames = new() { [TrackingType.Validation] = "Validation / FAI", [TrackingType.Fai] = "Validation / FAI", [TrackingType.Repair] = "Repair / Rework", [TrackingType.Production] = "Production", [TrackingType.Inspection] = "Inspection", [TrackingType.Warranty] = "Warranty", [TrackingType.SaltSpray] = "Salt Spray Test", [TrackingType.Development] = "Development" };
    public static string TypeName(TrackingType t) => TypeNames.TryGetValue(t, out var n) ? n : t.ToString();

    /// <summary>
    /// The names for the person filter: signed-in people and named day workers with recorded time on the given work order and/or in the period,
    /// on work orders matching the type, sub-type and product family filters.
    /// </summary>
    public static async Task<List<string>> PeopleAsync(AppDbContext db, IQueryable<SequflohTrackingRecord> records, Guid? workOrderId, ReportPeriod? p, TimeZoneInfo zone, CancellationToken ct)
    {
        if (workOrderId is null && p is null) return [];
        var q = records;
        if (workOrderId is { } wo) q = q.Where(r => r.Id == wo);
        DateTimeOffset? from = null, to = null;
        if (p is not null)
        {
            var (sd, ed) = p.Range();
            DateTimeOffset At(DateOnly d) { var t = d.ToDateTime(TimeOnly.MinValue); return new DateTimeOffset(t, zone.GetUtcOffset(t)).ToUniversalTime(); }
            from = At(sd); to = At(ed.AddDays(1));
            if (!string.IsNullOrWhiteSpace(p.Type)) { var ty = Enum.TryParse<TrackingType>(p.Type, true, out var tt) ? tt : throw new AppException("Unknown work order type."); q = ty is TrackingType.Validation or TrackingType.Fai ? q.Where(r => r.Type == TrackingType.Validation || r.Type == TrackingType.Fai) : q.Where(r => r.Type == ty); }
            if (!string.IsNullOrWhiteSpace(p.SubType)) q = q.Where(r => r.InspectionSubType == p.SubType);
            if (p.SiteId is { } site) q = q.Where(r => r.SiteId == site);
            if (p.FamilyId is { } fid) { var parts = db.Parts.Where(x => x.TypeId == fid).Select(x => x.Id); q = q.Where(r => r.PartId != null && parts.Contains(r.PartId.Value)); }
        }
        var ids = q.Select(r => r.Id);
        var times = db.SequflohTimeEntries.AsNoTracking().Where(t => ids.Contains(t.TrackingRecordId));
        var crews = db.SequflohCrewSessions.AsNoTracking().Where(s => s.WorkerName != null && ids.Contains(s.TrackingRecordId));
        if (from is { } f1 && to is { } t1) { times = times.Where(t => t.StartedAt >= f1 && t.StartedAt < t1); crews = crews.Where(s => s.StartedAt >= f1 && s.StartedAt < t1); }
        var names = await times.Select(t => t.UserName).Distinct().ToListAsync(ct);
        names.AddRange(await crews.Select(s => s.WorkerName!).Distinct().ToListAsync(ct));
        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Order(StringComparer.OrdinalIgnoreCase)];
    }

    sealed record Wo(SequflohTrackingRecord R, string? Family, long UserSec, long CrewSec, List<string> People);

    public static async Task<List<SequflohChargebackService.ReportBlock>> BlocksAsync(AppDbContext db, IQueryable<SequflohTrackingRecord> records, ReportPeriod p, HashSet<string> on, TimeZoneInfo zone, Func<DateTimeOffset?, string> local, DateTimeOffset now, CancellationToken ct)
    {
        var blocks = new List<SequflohChargebackService.ReportBlock>();
        if (!Keys.Any(on.Contains)) return blocks;
        var (sd, ed) = p.Range();
        DateTimeOffset At(DateOnly d) { var t = d.ToDateTime(TimeOnly.MinValue); return new DateTimeOffset(t, zone.GetUtcOffset(t)).ToUniversalTime(); }
        var from = At(sd); var to = At(ed.AddDays(1));
        TrackingType? type = string.IsNullOrWhiteSpace(p.Type) ? null : Enum.TryParse<TrackingType>(p.Type, true, out var tt) ? tt : throw new AppException("Unknown work order type.");

        var times = await db.SequflohTimeEntries.AsNoTracking().Where(t => t.StartedAt >= from && t.StartedAt < to).ToListAsync(ct);
        var crews = await db.SequflohCrewSessions.AsNoTracking().Where(s => s.StartedAt >= from && s.StartedAt < to).ToListAsync(ct);
        var person = p.Person?.Trim();
        if (!string.IsNullOrEmpty(person))
        {
            times = times.Where(t => string.Equals(t.UserName, person, StringComparison.OrdinalIgnoreCase)).ToList();
            crews = crews.Where(s => string.Equals(s.WorkerName, person, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var timedIds = times.Select(t => t.TrackingRecordId).Concat(crews.Select(s => s.TrackingRecordId)).ToHashSet();
        if (p.SiteId is { } site) records = records.Where(r => r.SiteId == site);
        var q = records.Where(r => timedIds.Contains(r.Id)
            || (string.IsNullOrEmpty(person) && ((r.CreatedAt >= from && r.CreatedAt < to) || (r.ActualCompletion >= from && r.ActualCompletion < to))));
        // Validation and FAI are one type to the user (Validation / FAI)
        if (type is { } ty) q = ty is TrackingType.Validation or TrackingType.Fai ? q.Where(r => r.Type == TrackingType.Validation || r.Type == TrackingType.Fai) : q.Where(r => r.Type == ty);
        if (!string.IsNullOrWhiteSpace(p.SubType)) q = q.Where(r => r.InspectionSubType == p.SubType);
        var recs = await q.ToListAsync(ct);
        var partIds = recs.Where(r => r.PartId != null).Select(r => r.PartId!.Value).Distinct().ToList();
        var partFam = await db.Parts.AsNoTracking().Where(x => partIds.Contains(x.Id)).Select(x => new { x.Id, x.TypeId }).ToDictionaryAsync(x => x.Id, x => x.TypeId, ct);
        var famNames = await db.SequflohReferenceValues.AsNoTracking().Where(v => v.ListKey == SequflohCatalog.ProductFamilies).ToDictionaryAsync(v => v.Id, v => v.Label, ct);
        Guid? FamOf(SequflohTrackingRecord r) => r.PartId is { } pid && partFam.TryGetValue(pid, out var f) ? f : null;
        if (p.FamilyId is { } fid) recs = recs.Where(r => FamOf(r) == fid).ToList();

        var ids = recs.Select(r => r.Id).ToHashSet();
        times = times.Where(t => ids.Contains(t.TrackingRecordId)).ToList();
        crews = crews.Where(s => ids.Contains(s.TrackingRecordId)).ToList();
        var wos = recs.Select(r => new Wo(r, FamOf(r) is { } f && famNames.TryGetValue(f, out var fn) ? fn : null,
            times.Where(t => t.TrackingRecordId == r.Id).Sum(t => t.Seconds), crews.Where(s => s.TrackingRecordId == r.Id).Sum(s => SequflohCrewService.Labor(s, now)),
            times.Where(t => t.TrackingRecordId == r.Id).Select(t => t.UserName).Concat(crews.Where(s => s.TrackingRecordId == r.Id && s.WorkerName != null).Select(s => s.WorkerName!)).Distinct().OrderBy(n => n).ToList()))
            .OrderBy(w => w.R.Number).ToList();
        long Tot(Wo w) => w.UserSec + w.CrewSec;
        var userSec = times.Sum(t => t.Seconds);
        var named = crews.Where(s => s.WorkerId != null).ToList(); var crew = crews.Where(s => s.WorkerId == null).ToList();
        var totalSec = wos.Sum(Tot);
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(person)) filters.Add($"person {person}");
        if (type is { } t2) filters.Add($"type {TypeName(t2)}");
        if (!string.IsNullOrWhiteSpace(p.SubType)) filters.Add($"sub-type {p.SubType}");
        if (p.SiteId is { } s2) filters.Add($"site {await db.Sites.Where(x => x.Id == s2).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? "unknown"}");
        if (p.FamilyId is { } f2) filters.Add($"product family {(famNames.TryGetValue(f2, out var fl) ? fl : "unknown")}");
        blocks.Add(new("period", "Period", [("Period", $"{p.Kind}: {p.Label()}"), ("Filters", filters.Count == 0 ? "none" : string.Join(", ", filters)), ("Work orders in the period", I(wos.Count))]));

        if (on.Contains("sumHours"))
            blocks.Add(new("sumHours", "Total hours", [("Total hours", H(totalSec) + " (paused time excluded)"), ("Signed-in people", H(userSec)),
                ("Named day workers", H(named.Sum(s => SequflohCrewService.Labor(s, now)))), ("Crew (labor hours = hours × crew size)", H(crew.Sum(s => SequflohCrewService.Labor(s, now))))]));

        List<string?[]> Group(IEnumerable<IGrouping<string, Wo>> g) =>
            [.. g.Select(x => new string?[] { x.Key, I(x.Count()), H(x.Sum(Tot)), totalSec > 0 ? (x.Sum(Tot) * 100m / totalSec).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "" })
                .OrderByDescending(r => decimal.Parse(r[2]!, CultureInfo.InvariantCulture)), ["Total", I(wos.Count), H(totalSec), ""]];
        (string, double)[] gc(string k) => [(k, 3), ("Work orders", 1.2), ("Hours", 1.2), ("Share", 1)];
        if (on.Contains("sumByType")) blocks.Add(new("sumByType", "Hours by work order type", Columns: [.. gc("Type")], Rows: Group(wos.GroupBy(w => TypeName(w.R.Type)))));
        if (on.Contains("sumBySubType")) blocks.Add(new("sumBySubType", "Hours by inspection sub-type", Columns: [.. gc("Sub-type")],
            Rows: Group(wos.Where(w => w.R.Type is TrackingType.Inspection or TrackingType.Warranty).GroupBy(w => w.R.InspectionSubType ?? "Not set"))));
        if (on.Contains("sumByFamily")) blocks.Add(new("sumByFamily", "Hours by product family", Columns: [.. gc("Product family")], Rows: Group(wos.GroupBy(w => w.Family ?? "Unassigned"))));

        var people = times.GroupBy(t => t.UserName).Select(g => (Name: g.Key, Kind: "Signed-in", Sec: g.Sum(t => t.Seconds), Paused: g.Sum(t => t.PausedSeconds), Wos: g.Select(t => t.TrackingRecordId).Distinct().Count()))
            .Concat(named.GroupBy(s => s.WorkerName ?? "").Select(g => (Name: g.Key, Kind: "Day worker", Sec: g.Sum(s => SequflohCrewService.Labor(s, now)), Paused: g.Sum(s => SequflohCrewService.Paused(s, now)), Wos: g.Select(s => s.TrackingRecordId).Distinct().Count())))
            .Concat(crew.Count == 0 ? [] : [(Name: "Crew", Kind: $"Crew ({crew.Sum(s => s.CrewSize)} worker sessions)", Sec: crew.Sum(s => SequflohCrewService.Labor(s, now)), Paused: crew.Sum(s => SequflohCrewService.Paused(s, now)), Wos: crew.Select(s => s.TrackingRecordId).Distinct().Count())])
            .OrderByDescending(x => x.Sec).ToList();
        if (on.Contains("sumByPerson"))
            blocks.Add(new("sumByPerson", "Hours by person", Columns: [("Person", 2.6), ("Kind", 1.8), ("Work orders", 1.1), ("Hours", 1), ("Paused hours (not counted)", 1.6)],
                Rows: [.. people.Select(x => new string?[] { x.Name, x.Kind, I(x.Wos), H(x.Sec), H(x.Paused) }), ["Total", "", "", H(people.Sum(x => x.Sec)), H(people.Sum(x => x.Paused))]]));

        if (on.Contains("sumCounts"))
        {
            bool Done(SequflohTrackingRecord r) => r.Status is TrackingStatus.Completed or TrackingStatus.Failed or TrackingStatus.Canceled;
            blocks.Add(new("sumCounts", "Work order counts", [("Opened in the period", I(recs.Count(r => r.CreatedAt >= from && r.CreatedAt < to))),
                ("Completed in the period", I(recs.Count(r => r.ActualCompletion >= from && r.ActualCompletion < to))), ("Still open", I(recs.Count(r => !Done(r)))),
                ("Overdue", I(recs.Count(r => !Done(r) && r.DueAt < now)))]));
        }
        if (on.Contains("sumQuality"))
        {
            var insp = recs.Where(r => r.Type is TrackingType.Inspection or TrackingType.Warranty).ToList();
            blocks.Add(new("sumQuality", "Inspection results", [("Inspection and warranty work orders", I(insp.Count)), ("Qty good", I(recs.Sum(r => r.QtyGood ?? 0))), ("Qty bad", I(recs.Sum(r => r.QtyBad ?? 0))),
                ("Quarantines", I(recs.Count(r => r.Quarantine))), ("Further inspection needed", I(recs.Count(r => r.FurtherInspection == true)))]));
        }
        if (on.Contains("sumChargebacks"))
        {
            var cbs = await db.SequflohChargebackReports.AsNoTracking().Where(c => c.CreatedAt >= from && c.CreatedAt < to && c.Period == null).ToListAsync(ct);
            if (p.SiteId is { } cs) cbs = cbs.Where(c => c.SiteId == cs).ToList();
            if (ids.Count > 0 && (type is not null || !string.IsNullOrWhiteSpace(p.SubType) || p.FamilyId is not null || !string.IsNullOrEmpty(person))) cbs = cbs.Where(c => c.TrackingRecordId is { } tid && ids.Contains(tid)).ToList();
            var totals = cbs.Select(SequflohChargebackService.TotalOf).ToList();
            blocks.Add(new("sumChargebacks", "Chargeback totals", [("Chargeback reports", I(cbs.Count)), ("Qty bad charged back", I(cbs.Sum(c => c.QtyBad ?? 0))),
                ("Hours charged back", H((long)(cbs.Sum(c => c.TotalHours ?? 0) * 3600))), ("Total chargeback", totals.Sum(x => x ?? 0).ToString("#,##0.00", CultureInfo.InvariantCulture))]));
        }
        if (on.Contains("sumPauses"))
        {
            var evs = await db.SequflohTaskEvents.AsNoTracking().Where(e => ids.Contains(e.TrackingRecordId)).OrderBy(e => e.CreatedAt).ToListAsync(ct);
            var rows = new List<(string Kind, string Reason, long Sec)>();
            foreach (var g in evs.GroupBy(e => e.TrackingRecordId))
            {
                var list = g.ToList(); var rec = recs.First(r => r.Id == g.Key);
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e.CreatedAt < from || e.CreatedAt >= to || e.ToStatus is not (TrackingStatus.Paused or TrackingStatus.OnHold)) continue;
                    var hold = e.ToStatus == TrackingStatus.OnHold;
                    var endSec = i + 1 < list.Count ? (hold ? list[i + 1].HoldSeconds : list[i + 1].PausedSeconds) : (hold ? rec.HoldSeconds : rec.PausedSeconds);
                    var startSec = hold ? e.HoldSeconds : e.PausedSeconds;
                    rows.Add((hold ? "Hold" : "Pause", string.IsNullOrWhiteSpace(e.Reason) ? "No reason given" : e.Reason!, Math.Max(0, endSec - startSec)));
                }
            }
            blocks.Add(new("sumPauses", "Pause and hold time by reason", Columns: [("Kind", 1), ("Reason", 3.5), ("Times", 1), ("Hours", 1)],
                Rows: [.. rows.GroupBy(r => (r.Kind, r.Reason)).Select(g => new string?[] { g.Key.Kind, g.Key.Reason, I(g.Count()), H(g.Sum(x => x.Sec)) }).OrderBy(r => r[0]).ThenBy(r => r[1]),
                    ["Total", "", I(rows.Count), H(rows.Sum(x => x.Sec))]]));
        }
        if (on.Contains("sumDetail"))
            blocks.Add(new("sumDetail", "Work order detail", Columns: [("WO #", 1.3), ("Type / sub-type", 1.5), ("Part", 1.3), ("Product", 1.2), ("Description", 2), ("People", 1.6), ("Start", 1.3), ("Finish", 1.3),
                    ("Active h", 0.8), ("Paused h", 0.8), ("Hold h", 0.8), ("Hours in period", 0.9), ("Qty good", 0.7), ("Qty bad", 0.7)],
                Rows: [.. wos.Select(w => new string?[] { w.R.Number, TypeName(w.R.Type) + (w.R.InspectionSubType is { } st ? $" / {st}" : ""), w.R.PartNumber ?? "", w.Family ?? "",
                    w.R.InspectionDescription ?? w.R.PartDescription ?? w.R.WorkInstructionTitle, string.Join(", ", w.People), local(w.R.ActualStart), local(w.R.ActualCompletion),
                    H(w.R.ActiveSeconds), H(w.R.PausedSeconds), H(w.R.HoldSeconds), H(Tot(w)), w.R.QtyGood?.ToString(CultureInfo.InvariantCulture) ?? "", w.R.QtyBad?.ToString(CultureInfo.InvariantCulture) ?? "" })]));
        if (on.Contains("sumCharts"))
        {
            var byType = wos.GroupBy(w => TypeName(w.R.Type)).Select(g => (g.Key, g.Sum(Tot))).ToList();
            var max = Math.Max(byType.Select(x => x.Item2).DefaultIfEmpty(0).Max(), people.Select(x => x.Sec).DefaultIfEmpty(0).Max());
            blocks.Add(new("sumCharts", "Where the time went", Columns: [("By", 1.2), ("Name", 2.2), ("Hours", 1), ("Chart", 4)],
                Rows: [.. byType.OrderByDescending(x => x.Item2).Select(x => new string?[] { "Type", x.Key, H(x.Item2), Bar(x.Item2, max) }),
                    .. people.Select(x => new string?[] { "Person", x.Name, H(x.Sec), Bar(x.Sec, max) })]));
        }
        return blocks;
    }
}
