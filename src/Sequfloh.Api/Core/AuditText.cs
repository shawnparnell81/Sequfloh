using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sequfloh.Infrastructure.Persistence;

/// <summary>Turns field names and values into the plain-language wording used in audit descriptions.</summary>
public static partial class AuditText
{
    public const int MaxDescription = 1000;
    const int MaxInlineText = 80;

    [GeneratedRegex("\u27E6([0-9a-fA-F-]{36})\\|([^\u27E7]*)\u27E7")]
    public static partial Regex RefPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    public static partial Regex GuidPattern();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex WordBreak();

    /// <summary>"ContainmentSummary" → "Containment summary"; "OwnerId" → "Owner"; "IsArchived" → "Archived"; "PpapLevel" → "Ppap level".</summary>
    public static string Label(string property, Type clrType)
    {
        var name = property;
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(Guid) && name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal)) name = name[..^2];
        if (type == typeof(bool) && name.Length > 2 && name.StartsWith("Is", StringComparison.Ordinal) && char.IsUpper(name[2])) name = name[2..];
        return Words(name);
    }

    public static string Words(string pascal)
    {
        var parts = WordBreak().Split(pascal).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return pascal;
        var sb = new StringBuilder(parts[0]);
        foreach (var p in parts.Skip(1))
            sb.Append(' ').Append(p.All(char.IsUpper) && p.Length > 1 ? p : p.ToLowerInvariant());
        return sb.ToString();
    }

    /// <summary>A short readable value, or null when the value is too long or can't be shown (the description then says it changed).</summary>
    public static string? Value(object? v, Func<Guid, string?> names) => v switch
    {
        null => null,
        string s when string.IsNullOrWhiteSpace(s) => null,
        string s => s.Length <= MaxInlineText && !s.Contains('\n') ? $"\"{s.Trim()}\"" : null,
        bool b => b ? "Yes" : "No",
        Enum e => string.Join(", ", e.ToString().Split(", ").Select(Words)),
        Guid g => names(g),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };

    static bool Blank(object? v) => v is null || v is string s && string.IsNullOrWhiteSpace(s);

    /// <summary>One changed field in words, for example "Status: Open → Contained" or "Containment summary set to "Lot 42 quarantined"".</summary>
    public static string Change(string label, object? from, object? to, Func<Guid, string?> names)
    {
        var f = Value(from, names);
        var t = Value(to, names);
        if (Blank(from) && !Blank(to)) return t is null ? $"{label} set" : $"{label} set to {t}";
        if (!Blank(from) && Blank(to)) return f is null ? $"{label} cleared" : $"{label} cleared (was {f})";
        if (f is null || t is null) return $"{label} changed";
        return $"{label}: {f} → {t}";
    }

    public static string Fit(string description) =>
        description.Length <= MaxDescription ? description : description[..(MaxDescription - 1)] + "…";

    /// <summary>Every id an audit label refers to, through <see cref="Domain.Common.AuditRef"/> or written out.</summary>
    public static IEnumerable<Guid> Ids(string label) =>
        RefPattern().Matches(label).Select(m => Guid.Parse(m.Groups[1].Value))
            .Concat(GuidPattern().Matches(RefPattern().Replace(label, "")).Select(m => Guid.Parse(m.Value)));

    /// <summary>Replaces references and written-out ids with readable names; a reference that can't be found uses its fallback.</summary>
    public static string Resolve(string label, Func<Guid, string?> names)
    {
        var s = RefPattern().Replace(label, m => names(Guid.Parse(m.Groups[1].Value)) ?? m.Groups[2].Value);
        s = GuidPattern().Replace(s, m => names(Guid.Parse(m.Value)) ?? m.Value);
        return Regex.Replace(s, " {2,}", " ").Trim();
    }
}
