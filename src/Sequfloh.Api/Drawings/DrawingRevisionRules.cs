namespace Sequfloh.Drawings;

/// <summary>
/// The drawing revision is the one a person types, or the one recorded in the Part Master if they leave it blank. It is never read off a file name.
/// A drawing number is kept whole: a trailing letter is part of the number. Some drawings, such as brake wear sensors, have no revision suffix.
/// </summary>
public static class DrawingRevisionRules
{
    public readonly record struct Resolved(string Revision, bool FromPartMaster);

    /// <summary>The drawing number as written, with surrounding spaces removed. Nothing is stripped from the end.</summary>
    public static string WholeNumber(string? value) => (value ?? "").Trim();

    public static List<string> Distinct(IEnumerable<string?> values) =>
        values.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Picks the revision to store. A typed value is kept as written. If none is typed and the Part Master has exactly one, that value is used.
    /// Several Part Master values, or none, leave the revision blank when nothing was typed. Nothing is rejected for format or disagreement.
    /// <paramref name="partMasterWins"/> and <paramref name="drawingLabel"/> are kept for callers; they do not change the result.
    /// </summary>
    public static Resolved Resolve(string? typed, IEnumerable<string?> partMasterRevisions, string drawingLabel, bool partMasterWins)
    {
        _ = drawingLabel;
        _ = partMasterWins;
        var typedClean = DrawingService.Revision(typed);
        if (typedClean.Length > 0) return new Resolved(typedClean, false);
        var fromParts = Distinct(partMasterRevisions);
        if (fromParts.Count == 1) return new Resolved(DrawingService.Revision(fromParts[0]), true);
        return new Resolved("", false);
    }

    /// <summary>A row that would replace a recorded drawing revision with a different one. Blank values fill in; they do not conflict.</summary>
    public static string? Conflict(string? recorded, string? incoming)
    {
        var have = (recorded ?? "").Trim();
        var file = (incoming ?? "").Trim();
        if (have.Length == 0 || file.Length == 0 || string.Equals(have, file, StringComparison.OrdinalIgnoreCase)) return null;
        return $"Drawing revision {have} is already recorded in the Part Master (the file has {file}). The row was not imported. The file name was not used as a revision.";
    }
}
