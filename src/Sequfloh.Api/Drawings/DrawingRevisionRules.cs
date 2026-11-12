using Sequfloh.Application.Abstractions;

namespace Sequfloh.Drawings;

/// <summary>
/// The drawing revision is the one recorded in the Part Master, or the revision a person types. It is never read off a file name.
/// A drawing number is kept whole: a trailing letter is part of the number. Some drawings, such as brake wear sensors, have no revision suffix.
/// </summary>
public static class DrawingRevisionRules
{
    /// <param name="Note">When set, the Part Master could not supply one usable revision and the typed import revision was stored instead. Put this in the revision notes and the audit description.</param>
    public readonly record struct Resolved(string Revision, bool FromPartMaster, string? Note = null);

    /// <summary>The drawing number as written, with surrounding spaces removed. Nothing is stripped from the end.</summary>
    public static string WholeNumber(string? value) => (value ?? "").Trim();

    public static List<string> Distinct(IEnumerable<string?> values) =>
        values.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Picks the revision to store. When the Part Master agrees on one usable revision, that revision is used.
    /// <paramref name="partMasterWins"/> is for a bulk import: the typed revision is the fallback when the Part Master has none,
    /// or when it does not have exactly one usable revision (an invalid format, or several different ones). A single upload still
    /// rejects a typed revision that disagrees with the Part Master, and still requires the Part Master value to be usable.
    /// </summary>
    public static Resolved Resolve(string? typed, IEnumerable<string?> partMasterRevisions, string drawingLabel, bool partMasterWins)
    {
        var fromParts = Distinct(partMasterRevisions);
        var typedClean = (typed ?? "").Trim();
        var label = string.IsNullOrWhiteSpace(drawingLabel) ? "this drawing" : drawingLabel.Trim();
        if (fromParts.Count > 1)
        {
            if (!partMasterWins)
                throw new AppException($"Drawing {label} is recorded at more than one revision in the Part Master ({string.Join(", ", fromParts)}). Nothing was stored. The file name was not used. Make those parts agree, then try again.");
            return Fallback(typedClean, label, $"The Part Master records more than one revision ({string.Join(", ", fromParts)}), so the revision entered for this import was used. The Part Master was not changed.");
        }
        if (fromParts.Count == 1)
        {
            if (DrawingService.TryRevision(fromParts[0], out var master))
            {
                if (!partMasterWins && typedClean.Length > 0 && !string.Equals(typedClean, fromParts[0], StringComparison.OrdinalIgnoreCase))
                    throw new AppException($"The Part Master records drawing {label} at revision {fromParts[0]}. Enter that revision. It is not taken from the file name.");
                return new Resolved(master, true);
            }
            if (!partMasterWins)
                throw new AppException("Use letters, numbers, '-' or '.' for the revision (up to 20).");
            return Fallback(typedClean, label, $"The Part Master revision '{fromParts[0]}' is not a usable revision, so the revision entered for this import was used. The Part Master was not changed.");
        }
        if (typedClean.Length == 0)
            throw new AppException($"Enter the revision for {label}. It is not taken from the file name, and the Part Master has no drawing revision for it.");
        return new Resolved(DrawingService.Revision(typedClean), false);
    }

    static Resolved Fallback(string typed, string label, string note)
    {
        if (typed.Length == 0)
            throw new AppException($"Enter the revision for {label}. It is not taken from the file name, and the Part Master has no usable drawing revision for it.");
        return new Resolved(DrawingService.Revision(typed), false, note);
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
