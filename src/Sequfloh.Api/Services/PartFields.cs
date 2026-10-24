namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// The field blocks of a part screen that can be marked Not applicable. A hidden field keeps its value and can be shown again at any time.
/// Keys are the fixed fields below plus every attribute key from PartAttributes.
/// </summary>
public static class PartFields
{
    public static readonly (string Key, string Label)[] Fixed =
    [
        ("partClass", "Part class"), ("isAssembly", "Assembly"), ("unitOfMeasure", "Unit of measure"), ("isRepairable", "Repairable"), ("isRepairPart", "Repair part"),
        ("isSerialized", "Serialized"), ("isLotControlled", "Lot controlled"), ("material", "Material"), ("drawingNumber", "DWG #"), ("specification", "Specification"), ("description", "Description"),
    ];

    public static IEnumerable<(string Key, string Label)> All => Fixed.Concat(PartAttributes.All.Select(a => (a.Key, a.Label)));

    static readonly HashSet<string> Placeholders = new(["/", "-", "--", "—", "N/A", "NA", "N.A.", "NONE", "TBD", "NULL", "."], StringComparer.OrdinalIgnoreCase);

    /// <summary>True for an empty value or a placeholder such as "/", "-" or "N/A".</summary>
    public static bool IsBlank(string? v) => string.IsNullOrWhiteSpace(v) || Placeholders.Contains(v.Trim());

    public static List<string> Read(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    /// <summary>Checks the keys and joins them; unknown keys are rejected.</summary>
    public static string? Write(IEnumerable<string>? keys)
    {
        var valid = All.Select(x => x.Key).ToHashSet();
        var l = (keys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
        foreach (var k in l) if (!valid.Contains(k)) throw new Application.Abstractions.AppException($"\"{k}\" is not a part field.");
        return l.Count == 0 ? null : string.Join(",", l.Order(StringComparer.Ordinal));
    }
}
