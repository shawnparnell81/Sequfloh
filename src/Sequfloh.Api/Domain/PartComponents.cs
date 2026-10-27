namespace Sequfloh.Domain.MasterData;

/// <summary>Which list of a part a linked part is on.</summary>
public enum PartLinkKind { PartsList = 0, RepairParts = 1 }

/// <summary>A part on another part's Parts list or Repair parts list. Removing the link never deletes either part.</summary>
public class PartComponentLink
{
    public Guid PartId { get; set; }
    public Guid ComponentPartId { get; set; }
    public PartLinkKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
