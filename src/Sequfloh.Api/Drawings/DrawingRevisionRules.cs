using Sequfloh.Application.Abstractions;

namespace Sequfloh.Drawings;

/// <summary>
/// The drawing revision is the one recorded in the Part Master, or the revision a person types. It is never read off a file name.
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
    /// Picks the revision to store. When the Part Master agrees on one revision, that revision is used.
    /// <paramref name="partMasterWins"/> is for a bulk import: the typed revision is only a fallback when the Part Master has none.
    /// A single upload rejects a typed revision that disagrees with the Part Master.
    /// </summary>
    public static Resolved Resolve(string? typed, IEnumerable<string?> partMasterRevisions, string drawingLabel, bool partMasterWins)
    {
        var fromParts = Distinct(partMasterRevisions);
        var typedClean = (typed ?? "").Trim();
        var label = string.IsNullOrWhiteSpace(drawingLabel) ? "this drawing" : drawingLabel.Trim();
        if (fromParts.Count > 1)
            throw new AppException($"Drawing {label} is recorded at more than one revision in the Part Master ({string.Join(", ", fromParts)}). Nothing was stored. The file name was not used. Make those parts agree, then try again.");
        if (fromParts.Count == 1)
        {
            if (!partMasterWins && typedClean.Length > 0 && !string.Equals(typedClean, fromParts[0], StringComparison.OrdinalIgnoreCase))
                throw new AppException($"The Part Master records drawing {label} at revision {fromParts[0]}. Enter that revision. It is not taken from the file name.");
            return new Resolved(DrawingService.Revision(fromParts[0]), true);
        }
        if (typedClean.Length == 0)
            throw new AppException($"Enter the revision for {label}. It is not taken from the file name, and the Part Master has no drawing revision for it.");
        return new Resolved(DrawingService.Revision(typedClean), false);
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
