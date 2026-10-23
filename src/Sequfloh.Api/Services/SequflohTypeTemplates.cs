using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Execution;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// One starter layout template for each work instruction type, tied to that type. Added at start-up when a template with the same name is missing,
/// so existing databases get them and an edited copy is never overwritten. Generic content only.
/// </summary>
public static class SequflohTypeTemplates
{
    sealed record Spec(WorkInstructionType Type, string Name, string Description, string Purpose, string Tools, (string Title, string Body, string Expected)[] Steps, string Close);

    static readonly (string, string, string) Identify = ("Verify identification", "Confirm part number, revision and quantity against the work order, drawing and container label.", "Part number, revision and quantity match.");
    static readonly (string, string, string) Visual = ("Visual inspection", "Inspect for damage, corrosion, contamination, missing features and workmanship defects under adequate lighting.", "No visible defects.");

    static readonly Spec[] Specs =
    [
        new(WorkInstructionType.Validation, "Starter - Validation", "Starter layout for a validation work instruction.",
            "Confirm that the process or product meets its defined requirements before it is used in production.",
            "Calibrated gauges and test equipment listed in the validation plan.",
            [Identify, ("Set up", "Set up the equipment and fixtures as described in the validation plan. Record the equipment used.", "Set-up matches the plan."),
             ("Run the validation", "Run the validation the number of times called for in the plan and record each result.", "Every run is recorded."),
             ("Compare to acceptance criteria", "Compare each result with the acceptance criteria in the plan.", "All results meet the criteria.")],
            "Record the overall result on the work order. If any result fails, record the failure and the follow-up action in the findings."),
        new(WorkInstructionType.ValidationFai, "Starter - Validation / FAI", "Starter layout for a combined validation and first article inspection.",
            "Validate the process and inspect the first article produced by it against the drawing.",
            "Calibrated gauges listed on the drawing; test equipment named in the validation plan.",
            [Identify, ("Run the validation", "Run the validation steps in the plan and record each result.", "Every run is recorded."),
             ("Inspect the first article", "Measure every characteristic on the drawing on the first part produced and record the actual value.", "Every characteristic is within tolerance."), Visual],
            "Record the overall validation and first article result on the work order. Record any nonconforming characteristic in the findings."),
        new(WorkInstructionType.Fai, "Starter - FAI", "Starter layout for a first article inspection.",
            "Verify that the first part made from a new or changed process, drawing revision or supplier conforms to every drawing requirement.",
            "Calibrated gauges listed on the drawing; inspection light; magnifier.",
            [Identify, ("Review the drawing", "Number every characteristic on the drawing (balloon the drawing) and list them in the characteristics table.", "Every characteristic is listed."),
             ("Dimensional inspection", "Measure each characteristic and record the actual value and the gauge used.", "Every dimension is within tolerance."), Visual,
             ("Material and process records", "Check material certificates and any special process records called out on the drawing.", "Records are present and match the drawing.")],
            "Record the overall FAI result on the work order. Record any nonconforming characteristic in the findings."),
        new(WorkInstructionType.RepairTest, "Starter - Repair Test", "Starter layout for testing a part after repair.",
            "Confirm that a repaired part performs as specified before it is returned to use.",
            "Functional test equipment for the part; calibrated gauges; inspection light.",
            [Identify, Visual, ("Functional test", "Run the functional test defined for the part and record each reading against its limits.", "All readings are within limits."),
             ("Dimensional check", "Measure any characteristics affected by the repair.", "Dimensions are within tolerance.")],
            "Record the test result on the work order. Enter the job-level quantities tested, good and bad on the Job tab. Parts that fail go back for further repair."),
        new(WorkInstructionType.RepairWork, "Starter - Repair Work Instruction", "Starter layout for a repair work instruction.",
            "Restore a repairable part to its specified condition using the approved repair method.",
            "Hand tools and fixtures listed in the operations; replacement parts listed in the parts table; calibrated gauges.",
            [Identify, ("Inspect as received", "Record the condition of the part as received and the reported failure.", "Condition is recorded."),
             ("Disassemble", "Disassemble only as far as needed to reach the failed area. Keep removed hardware together.", "Failed area is accessible."),
             ("Replace or repair", "Replace or repair the failed components. Record the parts replaced on the Job tab.", "Failed components are replaced or repaired."),
             ("Reassemble", "Reassemble the part, applying torque values and materials called out in the operations.", "Part is reassembled as specified.")],
            "Test the part using the repair test instruction. Enter the job-level quantities repaired and could not repair on the Job tab."),
    ];

    static JsonArray Blocks(Spec s)
    {
        var a = new JsonArray
        {
            new JsonObject { ["kind"] = "text", ["title"] = "Purpose", ["body"] = s.Purpose },
            new JsonObject { ["kind"] = "text", ["title"] = "Required tools and gauges", ["body"] = s.Tools },
            new JsonObject { ["kind"] = "caution", ["level"] = "note", ["body"] = "Use only gauges with a current calibration. Handle parts so the work does not cause damage." },
        };
        var op = 10;
        foreach (var (t, b, e) in s.Steps) { a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = t, ["body"] = b, ["expected"] = e }); op += 10; }
        a.Add(new JsonObject { ["kind"] = "step", ["operation"] = op.ToString(), ["title"] = "Record results", ["body"] = s.Close });
        return a;
    }

    public static IReadOnlyList<(WorkInstructionType Type, string Name)> All => [.. Specs.Select(s => (s.Type, s.Name))];

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var have = await db.SequflohInstructionTemplates.Select(t => t.Name).ToListAsync(ct);
        foreach (var s in Specs.Where(s => !have.Contains(s.Name)))
            db.SequflohInstructionTemplates.Add(new SequflohInstructionTemplate { Name = s.Name, Description = s.Description, Type = s.Type, Blocks = SequflohBlocks.Clean(Blocks(s)) });
        await db.SaveChangesAsync(ct);
    }
}
