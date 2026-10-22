namespace Sequfloh.Domain.Common;

/// <summary>Configurable numbering per record type and optional site. Allocated inside the caller's transaction with a row lock.</summary>
public class NumberSequence
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string RecordType { get; set; } = "";
    public Guid? SiteId { get; set; }
    public string Prefix { get; set; } = "";
    public int Padding { get; set; } = 4;
    public bool ResetYearly { get; set; }
    public int Year { get; set; }
    public long NextValue { get; set; } = 1;
}
