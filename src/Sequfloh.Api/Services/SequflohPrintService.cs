using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Controlled printed layouts for SEQUFLOH: Validation, Validation / FAI, FAI, Repair Test, Repair Work and Inspection Instructions,
/// built from the designer schema so every printed section matches the designer. Each print is audited.
/// </summary>
public sealed class SequflohPrintService(AppDbContext db, ModuleSupport m, ICurrentUser current, SequflohInstructionService wi, SequflohFoundationService f, SequflohTrackingService tracking, SequflohTestResultService tests)
{
    public async Task<string> OrganizationAsync(CancellationToken ct) => await db.OrganizationSettings.AsNoTracking().Select(s => s.OrganizationName).FirstOrDefaultAsync(ct) ?? "SEQUFLOH";

    public async Task<(string Printed, Func<DateTimeOffset?, string> Local)> ClockAsync(Guid? siteId, CancellationToken ct)
    {
        var zone = await m.ZoneAsync(siteId, ct);
        string Local(DateTimeOffset? t)
        {
            if (t is null) return "";
            var off = zone.GetUtcOffset(t.Value);
            return $"{TimeZoneInfo.ConvertTime(t.Value, zone):yyyy-MM-dd HH:mm} {(off == TimeSpan.Zero ? "UTC" : $"UTC{(off < TimeSpan.Zero ? "-" : "+")}{off:hh\\:mm}")}";
        }
        return (Local(m.Now), Local);
    }

    static string? V(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "Yes" : "No",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString("0.######", CultureInfo.InvariantCulture),
        _ => string.IsNullOrWhiteSpace(n.ToString()) ? null : n.ToString()
    };
    static string Words(WorkInstructionStatus s) => System.Text.RegularExpressions.Regex.Replace(s.ToString(), "([a-z])([A-Z])", "$1 $2");

    public async Task<(byte[] Pdf, string FileName)> InstructionAsync(Guid id, CancellationToken ct)
    {
        var x = await wi.LoadAsync(id, false, ct);
        var schema = SequflohDesigner.Schemas[x.Type];
        var settings = await f.GetSettingsAsync(ct);
        var (printed, local) = await ClockAsync(x.SiteId, ct);
        var ids = new[] { x.OwnerId, x.AuthorId, x.ReleasedById }.Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? N(Guid? u) => u is { } g && names.TryGetValue(g, out var n) ? n : null;
        var site = await db.Sites.AsNoTracking().Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefaultAsync(ct);
        var dept = x.DepartmentId is null ? null : await db.Departments.AsNoTracking().Where(d => d.Id == x.DepartmentId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        var content = JsonNode.Parse(x.Content)!.AsObject();
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Document control"),
            new PrintFields([("Instruction number", x.Number), ("Instruction type", schema.Title), ("Title", x.Title), ("Revision", x.Revision), ("Status", Words(x.Status)),
                ("Effective date", x.EffectiveDate?.ToString("yyyy-MM-dd")), ("Superseded", local(x.SupersededAt)), ("Obsolete", local(x.ObsoleteAt)), ("Site", site), ("Department", dept),
                ("Owner", N(x.OwnerId)), ("Author", N(x.AuthorId)), ("Created", local(x.CreatedAt)), ("Updated", local(x.UpdatedAt)),
                ("Released", local(x.ReleasedAt)), ("Revision reason", x.RevisionReason)]),
            new PrintHeading(x.Type == WorkInstructionType.RepairWork ? "Part identification and repair description" : "Applicability"),
            new PrintFields([(x.Type == WorkInstructionType.RepairWork ? "Part being repaired" : "Part number", x.PartNumber), ("Part description", x.PartDescription), ("Product family", x.ProductFamily),
                ("Assembly", x.Assembly), ("Model", x.Model), ("Variant", x.Variant), ("Process", x.Process), ("Operation", x.Operation), ("Workstation", x.Workstation), ("Location", x.Location),
                ("Repair description", x.RepairDescription)]),
        };
        foreach (var s in schema.Sections)
        {
            var src = content[s.Key] as JsonObject;
            var items = s.Fields.Select(fl => (fl.Label, V(src?[fl.Key]))).ToList();
            if (items.All(i => i.Item2 is null) && s.Key is "approval" or "validationApproval" or "faiDisposition") continue;
            blocks.Add(new PrintHeading(s.Title));
            blocks.Add(new PrintFields(items));
        }
        var layout = JsonNode.Parse(x.Blocks) as JsonArray ?? [];
        if (layout.Count > 0)
        {
            blocks.Add(new PrintHeading("Instruction"));
            var files = SequflohBlocks.ImageIds(x.Blocks);
            var fileNames = files.Count == 0 ? [] : await db.Attachments.AsNoTracking().Where(a => files.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.FileName, ct);
            foreach (var b in layout.OfType<JsonObject>())
            {
                switch (V(b["kind"]))
                {
                    case "text":
                        if (V(b["title"]) is { } tt) blocks.Add(new PrintFields([(tt, V(b["body"]))])); else blocks.Add(new PrintParagraph(V(b["body"]) ?? ""));
                        break;
                    case "step":
                        blocks.Add(new PrintFields([($"Operation {V(b["operation"])}", V(b["title"])), ("Instructions", V(b["body"])), ("Tools and materials", V(b["tools"])), ("Expected result", V(b["expected"]))]));
                        break;
                    case "caution":
                        blocks.Add(new PrintParagraph($"{(V(b["level"]) ?? "caution").ToUpperInvariant()}: {V(b["body"])}"));
                        break;
                    case "image":
                        var file = Guid.TryParse(V(b["attachmentId"]), out var fid) && fileNames.TryGetValue(fid, out var fn) ? fn : null;
                        blocks.Add(new PrintFields([("Visual aid", string.Join(" · ", new[] { V(b["caption"]), V(b["alt"]), file is null ? null : $"see attached file {file}" }.Where(v => v is not null)))]));
                        break;
                    case "table":
                        var cols = (b["columns"] as JsonArray ?? []).Select(c => (V(c) ?? "", 1.0)).ToList();
                        if (V(b["title"]) is { } title) blocks.Add(new PrintParagraph(title));
                        blocks.Add(new PrintTable(cols, (b["rows"] as JsonArray ?? []).Select(r => (r as JsonArray ?? []).Select(V).ToArray()).ToList()));
                        break;
                    case "check":
                        blocks.Add(new PrintFields([($"Check{(b["required"] is JsonValue rq && rq.TryGetValue<bool>(out var req) && req ? " (required)" : "")}", V(b["label"])),
                            ("Result", "______________________   Date ____________")]));
                        break;
                }
            }
        }
        blocks.Add(new PrintHeading(schema.StepsTitle));
        var steps = JsonNode.Parse(x.Steps)!.AsArray();
        if (steps.Count == 0) blocks.Add(new PrintParagraph("No steps."));
        foreach (var st in steps)
        {
            var items = new List<(string, string?)> { ($"Step {V(st?["sequence"])}", V(st?["title"]) ?? "") };
            items.AddRange(schema.StepColumns.Where(c => c.Key != "title").Select(c => (c.Label, V(st?[c.Key]))));
            blocks.Add(new PrintFields(items));
        }
        if (schema.HasCharacteristics)
        {
            blocks.Add(new PrintHeading("FAI characteristics"));
            var rows = JsonNode.Parse(x.Characteristics)!.AsArray().Select(c => new[]
            {
                V(c?["characteristicNumber"]), V(c?["drawingZone"]), V(c?["requirement"]), V(c?["specification"]), V(c?["target"]), V(c?["lowerLimit"]), V(c?["upperLimit"]), V(c?["unit"]),
                string.Join(" ", new[] { V(c?["inspectionMethod"]), V(c?["gaugeOrEquipment"]), V(c?["equipmentId"]) }.Where(s => s is not null)), V(c?["sampleQuantity"]), V(c?["actualResult"]), V(c?["result"])
            }).ToList();
            blocks.Add(new PrintTable([("No.", 0.6), ("Zone", 0.6), ("Requirement", 2.2), ("Specification", 1.4), ("Target", 0.8), ("Lower", 0.8), ("Upper", 0.8), ("Unit", 0.6), ("Method / gauge", 1.6),
                ("Qty", 0.5), ("Actual", 0.9), ("Result", 0.9)], rows));
        }
        if (schema.HasPartsRows)
        {
            blocks.Add(new PrintHeading("Repair parts"));
            var rows = JsonNode.Parse(x.PartsRows)!.AsArray().Select(p => new[]
            {
                V(p?["sequence"]), V(p?["replacementPartNumber"]), V(p?["description"]), V(p?["quantity"]), V(p?["unit"]), V(p?["required"]), V(p?["approvedAlternate"]), V(p?["lotNumberRequired"]),
                V(p?["serialNumberRequired"]), V(p?["issuedQuantity"]), V(p?["usedQuantity"]), V(p?["returnedQuantity"])
            }).ToList();
            blocks.Add(new PrintTable([("Item", 0.5), ("Part number", 1.4), ("Description", 2), ("Qty", 0.6), ("Unit", 0.6), ("Required", 0.8), ("Alternate", 1.1), ("Lot", 0.5), ("Serial", 0.6),
                ("Issued", 0.7), ("Used", 0.6), ("Returned", 0.8)], rows));
        }
        blocks.Add(new PrintHeading("Release"));
        blocks.Add(new PrintFields([("Prepared by", N(x.AuthorId)), ("Released by", x.ReleasedAt is null ? null : $"{N(x.ReleasedById)} · {local(x.ReleasedAt)}")]));
        blocks.Add(new PrintHeading("Revision history"));
        var revs = await db.SequflohWorkInstructions.AsNoTracking().Where(r => r.FamilyId == x.FamilyId).OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Revision, r.Status, r.EffectiveDate, r.ReleasedAt, r.SupersededAt, r.RevisionReason }).ToListAsync(ct);
        blocks.Add(new PrintTable([("Revision", 0.7), ("Status", 1), ("Effective", 1), ("Released", 1.4), ("Superseded", 1.4), ("Reason", 3)],
            revs.Select(r => new[] { r.Revision, Words(r.Status), r.EffectiveDate?.ToString("yyyy-MM-dd"), local(r.ReleasedAt), local(r.SupersededAt), r.RevisionReason }).ToList()));
        var transitions = await (from t in db.SequflohWorkInstructionTransitions.AsNoTracking() where t.WorkInstructionId == x.Id orderby t.CreatedAt
                                 select new { t.Action, t.FromStatus, t.ToStatus, t.Comment, t.CreatedAt, User = db.Users.Where(u => u.Id == t.UserId).Select(u => u.DisplayName).FirstOrDefault() }).ToListAsync(ct);
        blocks.Add(new PrintTable([("When", 1.3), ("Action", 1.1), ("From", 1), ("To", 1), ("User", 1.2), ("Comment", 2.6)],
            transitions.Select(t => new[] { local(t.CreatedAt), t.Action.Replace('-', ' '), t.FromStatus is null ? "" : Words(t.FromStatus.Value), Words(t.ToStatus), t.User, t.Comment }).ToList()));

        var header = new PrintHeader(await OrganizationAsync(ct), $"{schema.Title}: {x.Title}", x.Number, x.Revision, Words(x.Status), x.EffectiveDate?.ToString("yyyy-MM-dd"),
            x.PartNumber is null ? null : $"Part {x.PartNumber}{(x.PartDescription is null ? "" : " " + x.PartDescription)}", settings.WorkInstructions.PrintFooter, current.UserName, printed,
            x.Status == WorkInstructionStatus.Released ? null : Words(x.Status));
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("WorkInstructionPrinted", SequflohInstructionService.EntityType, x.Id, $"Printed {x.Number} rev {x.Revision} ({Words(x.Status).ToLowerInvariant()})", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"{Sequfloh.Imports.ImportedFileService.SafeFileName(x.Number)}.pdf");
    }

    static string W(object? s) => s is null ? "" : System.Text.RegularExpressions.Regex.Replace(s.ToString()!, "([a-z])([A-Z])", "$1 $2");
    static string Min(long sec) => $"{sec / 3600}h {sec % 3600 / 60:00}m";

    /// <summary>Printed tracking record: identity, references, timing, results, steps, characteristics, parts and the task event history.</summary>
    public async Task<(byte[] Pdf, string FileName)> TrackingAsync(Guid id, CancellationToken ct)
    {
        var x = await tracking.LoadAsync(id, false, ct);
        var settings = await f.GetSettingsAsync(ct);
        var (printed, local) = await ClockAsync(x.SiteId, ct);
        var ids = new[] { x.OwnerId, x.AssignedUserId, x.CompletedById }.Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? N(Guid? u) => u is { } g && names.TryGetValue(g, out var n) ? n : null;
        var site = await db.Sites.AsNoTracking().Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefaultAsync(ct);
        var typeTitle = x.Type switch { TrackingType.Fai => "Validation / FAI Tracking Record", TrackingType.SaltSpray => "Salt Spray Test Tracking Record", TrackingType.Inspection => "Inspection Tracking Record", TrackingType.Warranty => "Warranty Tracking Record", TrackingType.Repair => "Repair / Rework Tracking Record", TrackingType.Production => "Production Task", TrackingType.Development => "Development Tracking Record", TrackingType.Rebox => "Rebox Tracking Record", TrackingType.Test => "Test Tracking Record", TrackingType.Weight => "Weight Tracking Record", TrackingType.Research => "Research Tracking Record", _ => "Validation / FAI Tracking Record" };
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Record"),
            new PrintFields([("Tracking number", x.Number), ("Type", typeTitle), ("Status", W(x.Status)), ("Result", W(x.Result)), ("Priority", x.Priority),
                ("Work instruction", (string.IsNullOrEmpty(x.WorkInstructionNumber) ? "—" : $"{x.WorkInstructionNumber} rev {x.WorkInstructionRevision}")), ("Instruction title", x.WorkInstructionTitle), ("Site", site), ("Owner", N(x.OwnerId)), ("Assigned", N(x.AssignedUserId))]),
            new PrintHeading("References"),
            new PrintFields([("Part number", x.PartNumber), ("Part description", x.PartDescription), ("Serial number", x.SerialNumber), ("Lot number", x.LotNumber), ("Work order", x.WorkOrder),
                ("Repair order", x.RepairOrder), ("Customer return", x.CustomerReturnReference), ("Project", x.Project), ("Operation", x.Operation), ("FAI type", x.FaiType),
                ("Product family", x.FamilyName), ("Description", x.InspectionDescription),
                ("Validation form", x.Type is TrackingType.Test or TrackingType.Validation or TrackingType.SaltSpray ? SequflohTrackingService.FormLine(x) : null)]),
            new PrintHeading("Timing"),
            new PrintFields([("Planned start", local(x.PlannedStart)), ("Due", local(x.DueAt)), ("Actual start", local(x.ActualStart)), ("Actual completion", local(x.ActualCompletion)),
                ("Active time", Min(x.ActiveSeconds)), ("Paused time", Min(x.PausedSeconds)), ("On hold time", Min(x.HoldSeconds)),
                ("Benchmark (minutes)", x.MinBenchmarkMinutes is null && x.MaxBenchmarkMinutes is null ? null : $"{x.MinBenchmarkMinutes:0.##} to {x.MaxBenchmarkMinutes:0.##}"),
                ("Variance (minutes)", x.VarianceMinutes?.ToString("0.##", CultureInfo.InvariantCulture)), ("Variance reason", x.VarianceReason)]),
            new PrintHeading("Conditions and comments"),
            new PrintFields([("Reported condition", x.ReportedCondition), ("Observed condition", x.ObservedCondition), ("Repair description", x.RepairDescription), (x.Type == TrackingType.Fai ? "Inspection result" : "Final disposition", x.Type == TrackingType.Fai ? x.FinalDisposition : null),
                ("Deviations", x.Deviations), ("Nonconformance references", x.NonconformanceReferences), ("Evidence", x.EvidenceReferences), ("Technician comments", x.TechnicianComments),
                ("Quality comments", x.QualityComments), ("Engineering comments", x.EngineeringComments), ("Engineering conclusion", x.EngineeringConclusion),
                ("Pause reason", x.PauseReason), ("Failure reason", x.FailureReason), ("Cancel reason", x.CancelReason)]),
        };
        blocks.AddRange(await tests.PrintBlocksAsync(x, local, ct));
        var steps = JsonNode.Parse(x.Steps)!.AsArray();
        blocks.Add(new PrintHeading("Steps"));
        blocks.Add(steps.Count == 0 ? new PrintParagraph("No steps.") : new PrintTable([("Seq", 0.5), ("Step", 2.6), ("Result", 0.9), ("Actual", 1), ("Comment", 2)],
            steps.Select(st => new[] { V(st?["sequence"]), V(st?["title"]), V(st?["result"]), V(st?["actualResult"]) ?? V(st?["actual"]), V(st?["comment"]) }).ToList()));
        var chars = JsonNode.Parse(x.Characteristics)!.AsArray();
        if (chars.Count > 0)
        {
            blocks.Add(new PrintHeading("Characteristics"));
            blocks.Add(new PrintTable([("No.", 0.6), ("Requirement", 2.4), ("Lower", 0.8), ("Upper", 0.8), ("Unit", 0.6), ("Actual", 1), ("Result", 0.9)],
                chars.Select(c => new[] { V(c?["characteristicNumber"]), V(c?["requirement"]), V(c?["lowerLimit"]), V(c?["upperLimit"]), V(c?["unit"]), V(c?["actualResult"]), V(c?["result"]) }).ToList()));
        }
        var partsRows = JsonNode.Parse(x.PartsRows)!.AsArray();
        if (partsRows.Count > 0)
        {
            blocks.Add(new PrintHeading("Parts"));
            blocks.Add(new PrintTable([("Item", 0.5), ("Part number", 1.4), ("Description", 2.2), ("Qty", 0.6), ("Lot", 0.9), ("Serial", 0.9), ("Used", 0.6)],
                partsRows.Select(pr => new[] { V(pr?["sequence"]), V(pr?["replacementPartNumber"]) ?? V(pr?["partNumber"]), V(pr?["description"]), V(pr?["quantity"]), V(pr?["lotNumber"]), V(pr?["serialNumber"]), V(pr?["usedQuantity"]) }).ToList()));
        }
        blocks.Add(new PrintFields([("Completed by", N(x.CompletedById)), ("Completed", local(x.ActualCompletion))]));
        var events = await db.SequflohTaskEvents.AsNoTracking().Where(e => e.TrackingRecordId == x.Id).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        blocks.Add(new PrintHeading("Task events"));
        blocks.Add(new PrintTable([("When", 1.3), ("Event", 1), ("From", 1), ("To", 1), ("User", 1.2), ("Reason / comment", 2.5)],
            events.Select(e => new[] { local(e.CreatedAt), W(e.EventType), W(e.FromStatus), W(e.ToStatus), e.UserName, e.Reason ?? e.Comment }).ToList()));
        var terminal = x.Status is TrackingStatus.Completed or TrackingStatus.Failed or TrackingStatus.Canceled;
        var header = new PrintHeader(await OrganizationAsync(ct), $"{typeTitle}: {(string.IsNullOrEmpty(x.WorkInstructionTitle) ? x.Number : x.WorkInstructionTitle)}", x.Number, x.WorkInstructionRevision, W(x.Status), null,
            x.PartNumber is null ? null : $"Part {x.PartNumber}{(x.PartDescription is null ? "" : " " + x.PartDescription)}", settings.WorkInstructions.PrintFooter, current.UserName, printed, terminal ? null : W(x.Status), x.Number);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("TrackingRecordPrinted", SequflohTrackingService.EntityType, x.Id, $"Printed {x.Number} ({W(x.Status).ToLowerInvariant()})", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"{Sequfloh.Imports.ImportedFileService.SafeFileName(x.Number)}.pdf");
    }

    /// <summary>
    /// The everyday work order print: the normal work order information on one page (two for big orders). Leaves out the task event history,
    /// time entries and attachment contents, which stay on the full record print and the job report. Audited.
    /// </summary>
    public async Task<(byte[] Pdf, string FileName)> WorkOrderAsync(Guid id, CancellationToken ct)
    {
        var x = await tracking.LoadAsync(id, false, ct);
        var settings = await f.GetSettingsAsync(ct);
        var (printed, local) = await ClockAsync(x.SiteId, ct);
        var ids = new[] { x.OwnerId, x.AssignedUserId, x.CompletedById, x.CreatedById }.Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? N(Guid? u) => u is { } g && names.TryGetValue(g, out var n) ? n : null;
        string? Date(DateTimeOffset? t) => t is null ? null : local(t)[..10];
        static string Cut(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "...";
        var site = await db.Sites.AsNoTracking().Where(s => s.Id == x.SiteId).Select(s => s.Code + " · " + s.Name).FirstOrDefaultAsync(ct);
        var drawings = await db.TrackingRecordDrawings.AsNoTracking().Where(a => a.TrackingRecordId == x.Id).Join(db.Drawings, a => a.DrawingId, d => d.Id, (a, d) => new { d.DrawingNumber, d.Title, a.AttachedRevision })
            .ToListAsync(ct);
        var type = System.Text.RegularExpressions.Regex.Replace(x.Type switch { TrackingType.Fai => "Validation / FAI", TrackingType.SaltSpray => "Salt Spray", _ => x.Type.ToString() }, "([a-z])([A-Z])", "$1 $2");
        var findings = SequflohJobService.FindingsOf(x.Findings);
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Work order"),
            new PrintFields([("Work order number", x.Number), ("Type", type), ("Status", W(x.Status)), ("Result", x.Result is null ? null : W(x.Result)), ("Priority", x.Priority), ("Site", site),
                ("Created", local(x.CreatedAt)), ("Created by", N(x.CreatedById)), ("Owner", N(x.OwnerId)), ("Assigned", N(x.AssignedUserId))]),
            new PrintHeading("Part and references"),
            new PrintFields([("Part number", x.PartNumber), ("Part description", x.PartDescription), ("Product family", x.FamilyName), ("Serial number", x.SerialNumber), ("Lot number", x.LotNumber),
                ("Customer work order", x.WorkOrder), ("Repair order", x.RepairOrder), ("Quantity", x.QuantityTarget is null && x.QuantityCompleted == 0 ? null : $"{x.QuantityCompleted} completed{(x.QuantityTarget is { } q ? $" of {q}" : "")}"),
                ("Work instruction", string.IsNullOrEmpty(x.WorkInstructionNumber) ? "None" : $"{x.WorkInstructionNumber} rev {x.WorkInstructionRevision} {x.WorkInstructionTitle}".Trim()),
                ("Drawings", drawings.Count == 0 ? "None" : string.Join("; ", drawings.Select(d => $"{d.DrawingNumber} {d.Title} rev {d.AttachedRevision}".Trim()))),
                ("Validation form", x.Type is TrackingType.Test or TrackingType.Validation or TrackingType.SaltSpray ? SequflohTrackingService.FormLine(x) : null),
                ("Description", x.InspectionDescription is null ? null : Cut(x.InspectionDescription, 600))]),
            new PrintHeading("Dates"),
            new PrintFields([("Planned start", Date(x.PlannedStart)), ("Due", Date(x.DueAt)), ("Started", local(x.ActualStart)), ("Completed", local(x.ActualCompletion)), ("Completed by", N(x.CompletedById)),
                ("Active time", Min(x.ActiveSeconds))]),
        };
        var steps = JsonNode.Parse(x.Steps)!.AsArray();
        if (steps.Count > 0)
        {
            var done = steps.Count(st => V(st?["result"]) is not null);
            blocks.Add(new PrintHeading($"Steps ({done} of {steps.Count} recorded)"));
            blocks.Add(new PrintTable([("Seq", 0.5), ("Step", 3.4), ("Result", 1)], steps.Take(20).Select(st => new[] { V(st?["sequence"]), V(st?["title"]) is { } tt ? Cut(tt, 120) : null, V(st?["result"]) ?? "-" }).ToList()));
            if (steps.Count > 20) blocks.Add(new PrintParagraph($"{steps.Count - 20} more step(s); see the full record."));
        }
        if (x.Type == TrackingType.Test) blocks.AddRange(await tests.CompactBlocksAsync(x, ct));
        var notes = await db.SequflohJobNotes.AsNoTracking().Where(n => n.TrackingRecordId == x.Id).OrderByDescending(n => n.CreatedAt).Take(3).Select(n => new { n.Text, n.AuthorName, n.CreatedAt }).ToListAsync(ct);
        blocks.Add(new PrintHeading("Latest job notes"));
        if (notes.Count == 0) blocks.Add(new PrintParagraph("No job notes."));
        foreach (var n in notes) blocks.Add(new PrintParagraph($"{local(n.CreatedAt)} · {n.AuthorName}: {Cut(n.Text, 400)}"));
        blocks.Add(new PrintHeading("Findings / nonconformance"));
        blocks.Add(new PrintFields([("Findings", findings.HasFindings == true ? "Yes" : "No"), ("Description", findings.HasFindings == true && findings.Description is { } fd ? Cut(fd, 400) : null),
            ("Comments", x.TechnicianComments is null ? null : Cut(x.TechnicianComments, 400))]));
        var terminal = x.Status is TrackingStatus.Completed or TrackingStatus.Failed or TrackingStatus.Canceled;
        var header = new PrintHeader(await OrganizationAsync(ct), $"{type} Work Order", x.Number, null, W(x.Status), null,
            x.PartNumber is null ? null : $"Part {x.PartNumber}{(x.PartDescription is null ? "" : " " + x.PartDescription)}", settings.WorkInstructions.PrintFooter, current.UserName, printed, terminal ? null : W(x.Status), x.Number);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("WorkOrderPrinted", SequflohTrackingService.EntityType, x.Id, $"Printed work order {x.Number} ({W(x.Status).ToLowerInvariant()})", x.SiteId);
        await db.SaveChangesAsync(ct);
        return (pdf, $"{Sequfloh.Imports.ImportedFileService.SafeFileName(x.Number)}.pdf");
    }

    /// <summary>Printable audit record summary: the filtered audit events of one view (who, what, when, description), newest first.</summary>
    public async Task<(byte[] Pdf, string FileName)> AuditSummaryAsync(string title, IReadOnlyList<Domain.Audit.AuditEntry> rows, IReadOnlyList<(string, string)> filters, CancellationToken ct)
    {
        var settings = await f.GetSettingsAsync(ct);
        var (printed, local) = await ClockAsync(null, ct);
        var blocks = new List<PrintBlock>
        {
            new PrintHeading("Summary"),
            new PrintFields([("View", title), ("Events", rows.Count.ToString(CultureInfo.InvariantCulture)), ("Filters", filters.Count == 0 ? "None" : string.Join("; ", filters.Select(x => $"{x.Item1}: {x.Item2}")))]),
            new PrintHeading("Audit events"),
            new PrintTable([("When", 1.3), ("Who", 1.2), ("What", 1.3), ("Description", 4), ("Reason", 1.4)],
                rows.Select(a => new[] { local(a.OccurredAt), a.UserName, W(a.Action), a.Description, a.Reason }).ToList()),
        };
        var header = new PrintHeader(await OrganizationAsync(ct), $"Audit Record Summary: {title}", "SEQUFLOH audit", null, "Record", null, null, settings.WorkInstructions.PrintFooter, current.UserName, printed, null);
        var pdf = SequflohPdfRenderer.Render(header, blocks);
        f.Audit("AuditSummaryPrinted", "SequflohAudit", Guid.Empty, $"Printed the {title} audit summary ({rows.Count} events)");
        await db.SaveChangesAsync(ct);
        return (pdf, "sequfloh-audit-summary.pdf");
    }
}
