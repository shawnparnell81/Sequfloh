using System.Text.Json;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// One cross-reference number or other attribute of a part. Types lists the part type codes the field belongs to; null means every type.
/// Words are the column headings that suggest the field in Parts import.
/// </summary>
public sealed record PartAttributeDef(string Key, string Label, string[] Words, int MaxLength = 100, string[]? Types = null);

/// <summary>
/// The attribute fields of a part. The OR # is the part number and the SENSEN # is the drawing number, so neither is an attribute.
///  Values are stored together as JSON on the part, so a new field is one line here: it then appears on the part form,
/// details, list columns, search, CSV export and Parts import without a database change.
/// </summary>
public static class PartAttributes
{
    public static readonly PartAttributeDef[] All =
    [
        new("nissanVa", "Nissan VA #", ["nissan va", "nissan va #", "nissan va no", "nissan va number", "nissan va part number"]),
        new("napaUs", "NAPA US #", ["napa us", "napa us #", "napa us no", "napa us number", "napa usa"]),
        new("napaCa", "NAPA CA #", ["napa ca", "napa ca #", "napa ca no", "napa ca number", "napa canada"]),
        new("keyApplication", "Key Application", ["key application", "key applications", "key app", "application", "applications"], 1000),
        new("position", "Position", ["position", "pos", "location on vehicle", "mounting position"]),
    ];

    public static PartAttributeDef? Find(string key) => All.FirstOrDefault(a => a.Key == key);

    public static Dictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; } catch (JsonException) { return []; }
    }

    /// <summary>Checks and trims the values, then writes them to the part. Unknown keys are rejected; empty values are removed.</summary>
    public static void Write(Domain.MasterData.Part p, IDictionary<string, string?> values) => Store(p, Clean(values));

    /// <summary>The checked, trimmed values; throws on an unknown key or a value that is too long.</summary>
    public static Dictionary<string, string> Clean(IDictionary<string, string?> values)
    {
        var clean = new Dictionary<string, string>();
        foreach (var (key, raw) in values)
        {
            var def = Find(key) ?? throw new AppException($"\"{key}\" is not a part attribute.");
            var v = raw?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (v.Length > def.MaxLength) throw new AppException($"{def.Label} is limited to {def.MaxLength} characters.");
            clean[key] = v;
        }
        return clean;
    }

    /// <summary>Sets the given values and keeps the others (used by import, which never blanks existing data).</summary>
    public static void Merge(Domain.MasterData.Part p, IDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        var all = Read(p.Attributes);
        foreach (var (k, v) in values) all[k] = v;
        Store(p, all);
    }

    static void Store(Domain.MasterData.Part p, Dictionary<string, string> values)
    {
        var ordered = All.Where(a => values.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => values[a.Key]);
        p.Attributes = ordered.Count == 0 ? null : JsonSerializer.Serialize(ordered);
        var text = string.Join(" ", ordered.Values);
        p.AttributeText = text.Length == 0 ? null : text.Length > 4000 ? text[..4000] : text;
    }
}
