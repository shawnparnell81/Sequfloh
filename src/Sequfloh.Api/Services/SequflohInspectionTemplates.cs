using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Standard inspection layout templates for Inspection work orders. Added at start-up when a template with the same name is missing,
/// so existing databases get them and an edited copy is never overwritten. Generic steps only; holds, quarantine and inventory moves are made in NetSuite.
/// </summary>
public static class SequflohInspectionTemplates
{
    sealed record Spec(string Name, string Description, string Purpose, string Tools, (string Title, string Body, string Expected)[] Steps, string? Note = null);

    const string Results = "Record the result of each step on the work order. Enter the job-level quantities inspected, good and bad on the Job tab.";
    const string Segregate = "Keep good and nonconforming parts in separate, clearly labelled containers at the inspection bench until the job is closed out.";
    const string NextStep = "If any parts fail, create a repair work order for the failed quantity and reference this inspection work order. The repair work order includes the test of each part after repair.";
    const string Erp = "Holds, quarantine, scrap and inventory moves are not made here; they are handled in NetSuite.";

    static readonly (string, string, string) Visual = ("Visual inspection", "Inspect each part under adequate lighting for damage, corrosion, contamination, missing features, incorrect markings and workmanship defects.", "No visible defects.");
    static readonly (string, string, string) Dimensional = ("Dimensional inspection", "Measure the characteristics called out on the drawing or the linked work instruction. Use calibrated gauges only.", "All dimensions within drawing tolerance.");
    static readonly (string, string, string) Functional = ("Functional check", "Perform the functional or fit check defined for the part (operation, continuity, leak or fit as applicable).", "Part functions as specified.");
    static readonly (string, string, string) Docs = ("Verify identification", "Confirm part number, revision and quantity against the work order and the paperwork or label on the container.", "Part number, revision and quantity match.");

    static readonly Spec[] Specs =
    [
        new("Inspection - AQL Sample Result", "Sort the remaining inventory of a lot that failed AQL sampling.",
            "A sample from the lot failed the acceptance sampling plan. Inspect the remaining inventory to separate conforming parts from nonconforming parts.",
            "Calipers, micrometers or other calibrated gauges listed on the drawing; magnifier; inspection light.",
            [Docs, ("Review the sampling result", "Record the sampling plan reference (standard, inspection level and AQL), lot size, sample size, accept and reject numbers and the defects found in the sample.", "Failure mode is understood before sorting starts."),
             ("Sort for the failure mode", "Inspect every remaining part for the defect found in the sample.", "Each part is classed as good or nonconforming."),
             Visual, Dimensional]),
        new("Inspection - Product Alert", "Inspect stock affected by an internal or supplier product alert.",
            "A product alert identified a possible defect. Inspect the affected parts for the condition described in the alert.",
            "Gauges and equipment named in the alert; inspection light; magnifier.",
            [Docs, ("Review the alert", "Read the alert and note the affected part numbers, revisions, date codes and the condition to inspect for.", "Inspection criteria are clear."), Visual, Dimensional, Functional]),
        new("Inspection - Validation Result", "Inspect parts following a failed or questionable validation result.",
            "A validation run produced a result that needs confirming. Inspect the related parts against the validation criteria.",
            "Equipment used in the validation; calibrated gauges; inspection light.",
            [Docs, ("Review the validation result", "Note which validation step failed or was questionable and the acceptance criteria that apply.", "Criteria for this inspection are identified."), Visual, Dimensional, Functional]),
        new("Inspection - Warranty Claim Return", "Inspect a part returned under a warranty claim.",
            "Determine the condition of a returned part and whether the reported failure can be confirmed.",
            "Calibrated gauges; functional test equipment for the part; camera for evidence photos.",
            [Docs, ("Record the reported condition", "Record the failure reported by the customer and any visible shipping damage before handling.", "Reported condition is documented."),
             ("Photograph as received", "Take photos of the part as received, including labels and any damage.", "Evidence photos are attached."), Visual, Functional,
             ("Confirm the failure", "State whether the reported failure was confirmed, not confirmed, or a different failure was found.", "Finding is recorded.")]),
        new("Inspection - Supplier Request", "Inspect parts at the request of a supplier.",
            "A supplier asked for parts to be inspected, for example to confirm a suspect lot or a corrective action.",
            "Gauges and equipment listed in the supplier request; inspection light.",
            [Docs, ("Review the request", "Note the supplier, the reason for the request and the characteristics to inspect.", "Scope is clear."), Visual, Dimensional]),
        new("Inspection - Customer Request / Complaint", "Inspect stock in response to a customer request or complaint.",
            "A customer asked for parts to be inspected or reported a problem. Inspect the remaining stock for the reported condition.",
            "Calibrated gauges; functional test equipment if needed; camera.",
            [Docs, ("Review the request or complaint", "Note the customer reference, the reported condition and any characteristics the customer named.", "Inspection criteria are clear."), Visual, Dimensional, Functional]),
        new("Inspection - Random Inspection", "Routine random inspection of stock.",
            "Check a randomly selected quantity of parts to confirm stock continues to conform.",
            "Calibrated gauges listed on the drawing; inspection light.",
            [Docs, ("Select the parts", "Select parts at random from the stock location and record the quantity selected.", "Selection is random and recorded."), Visual, Dimensional]),
        new("Inspection - Recall / Containment Sort", "Inspect 100% of the remaining inventory for a recall or containment.",
            "Inspect every part of the remaining inventory for the suspect condition so only good parts are used.",
            "Gauges and equipment named in the containment notice; inspection light; magnifier.",
            [Docs, ("Review the containment notice", "Note the suspect condition, the affected part numbers, revisions and date codes.", "Suspect condition is understood."),
             ("Inspect 100%", "Inspect every part in the remaining inventory for the suspect condition. Do not sample.", "Every part is classed as good or nonconforming."), Visual, Dimensional],
            Erp),
    ];

    static JsonArray Blocks(Spec s)
    {
        var a = new JsonArray
        {
            new JsonObject { ["kind"] = "text", ["title"] = "Purpose", ["body"] = s.Purpose },
            new JsonObject { ["kind"] = "text", ["title"] = "Required tools and gauges", ["body"] = s.Tools },
            new JsonObject { ["kind"] = "caution", ["level"] = "note", ["body"] = "Use only gauges with a current calibration. Handle parts so inspection does not cause damage." },
        };
        var op = 10;
        foreach (var (t, b, e) in s.Steps) { a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = t, ["body"] = b, ["expected"] = e }); op += 10; }
        a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = "Record results", ["body"] = Results }); op += 10;
        a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = "Segregate counts", ["body"] = Segregate }); op += 10;
        a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = "Next step", ["body"] = NextStep });
        a.Add(new JsonObject { ["kind"] = "caution", ["level"] = "note", ["body"] = s.Note ?? Erp });
        return a;
    }

    public static IReadOnlyList<string> Names => [.. Specs.Select(s => s.Name)];

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var have = await db.SequflohInstructionTemplates.Select(t => t.Name).ToListAsync(ct);
        foreach (var s in Specs.Where(s => !have.Contains(s.Name)))
            db.SequflohInstructionTemplates.Add(new SequflohInstructionTemplate { Name = s.Name, Description = s.Description, Type = WorkInstructionType.Inspection, Blocks = SequflohBlocks.Clean(Blocks(s)) });
        // Templates seeded before the Inspection type existed have no type: file them under Inspection. Only untyped rows are touched.
        var names = Names.ToList();
        foreach (var t in await db.SequflohInstructionTemplates.Where(t => t.Type == null && names.Contains(t.Name)).ToListAsync(ct))
            t.Type = WorkInstructionType.Inspection;
        await db.SaveChangesAsync(ct);
    }
}
