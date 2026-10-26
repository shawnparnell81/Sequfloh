using System.Text.Json;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// One cross-reference number or other attribute of a part. Types lists the part type codes the field belongs to (null means every type); NotTypes lists codes it is not shown for.
/// Words are the column headings that suggest the field in Parts import.
/// </summary>
/// Verbatim fields keep a placeholder such as "/" as written; it is shown as is and is still never a match key.
/// DrawingNumber marks the field that holds the drawing number for the part types it belongs to, so drawings named with that number link to the part.
public sealed record PartAttributeDef(string Key, string Label, string[] Words, int MaxLength = 100, string[]? Types = null, string[]? NotTypes = null, bool DrawingNumber = false, bool Verbatim = false);

/// <summary>
/// The attribute fields of a part. The SENSEN # is the drawing number, so it is not an attribute.
///  Values are stored together as JSON on the part, so a new field is one line here: it then appears on the part form,
/// details, list columns, search, CSV export and Parts import without a database change.
/// </summary>
public static class PartAttributes
{
    static readonly string[] FuelPump = ["FUEL_PUMP"];
    /// <summary>Fuel injectors use the fuel pump drawing number (JH PN) as their drawing number too.</summary>
    static readonly string[] FuelPumpAndInjector = ["FUEL_PUMP", "FUEL_INJECTOR"];

    public static readonly PartAttributeDef[] All =
    [
        // the O'Reilly's customer part number, kept exactly as written: "/" means O'Reilly's does not carry the part
        new("orNumber", "OR #", ["or pn", "or #", "or#", "or no", "or number", "or part number", "or part #", "oreilly", "oreillys", "oreilly #", "oreilly pn", "oreilly part number", "oreillys #", "oreillys pn", "oreillys part number"], Verbatim: true),
        new("nissanVa", "Nissan VA #", ["nissan va", "nissan va #", "nissan va no", "nissan va number", "nissan va part number"], NotTypes: FuelPump),
        new("napaUs", "NAPA US #", ["napa us", "napa us #", "napa us no", "napa us number", "napa usa"], NotTypes: FuelPump),
        new("napaCa", "NAPA CA #", ["napa ca", "napa ca #", "napa ca no", "napa ca number", "napa canada"], NotTypes: FuelPump),
        new("keyApplication", "Key Application", ["key application", "key applications", "key app", "application", "applications"], 1000, NotTypes: FuelPump),
        new("position", "Position", ["position", "pos", "location on vehicle", "mounting position"], NotTypes: FuelPump),
        // the columns of the fuel pump sheet besides OR PN
        new("napaPn", "NAPA PN", ["napa pn"], Types: FuelPump),
        new("dmaPn", "DMA PN", ["dma pn"], Types: FuelPump),
        new("cross", "Cross", ["cross"], Types: FuelPump),
        // the JH PN is the drawing of the fuel pump; many parts share one
        new("jhPn", "JH PN", ["jh pn"], Types: FuelPumpAndInjector, DrawingNumber: true),
        new("boxSize", "Individual Box Size", ["individual box size"], Types: FuelPump),
    ];

    /// <summary>The attribute fields that hold a drawing number. The SENSEN # (the part's drawing number column) always counts as well.</summary>
    public static IEnumerable<PartAttributeDef> DrawingNumberFields => All.Where(a => a.DrawingNumber);

    /// <summary>True when the field is shown for parts of the family with this code (null code: a part without a family).</summary>
    public static bool AppliesTo(PartAttributeDef d, string? code) =>
        (d.Types is not { Length: > 0 } || (code is not null && d.Types.Contains(code))) && !(code is not null && d.NotTypes is { } n && n.Contains(code));

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
