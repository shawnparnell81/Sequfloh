namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// The fixed choices of the part form and Parts import. Name, Product family and Part class use the admin-managed product families list instead.
/// Values are matched without regard to case.
/// </summary>
public static class PartChoices
{
    public static readonly string[] Units = ["Each", "Lot", "Batch", "Skid"];
    public static readonly string[] YesNo = ["Yes", "No"];
    public static readonly string[] LotControl = ["Yes", "No", "Not applicable"];

    /// <summary>The unit for a typed value, or null when it is not one of the choices. "EA" is read as Each.</summary>
    public static string? Unit(string v) => v.Trim().ToLowerInvariant() switch
    {
        "ea" or "each" => "Each", "lot" => "Lot", "batch" => "Batch", "skid" => "Skid", _ => null,
    };

    public static bool? Flag(string v) => v.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "1" or "x" => true, "n" or "no" or "false" or "0" => false, _ => null,
    };

    public static string? Lot(string v) => v.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "true" or "1" or "x" => "Yes", "n" or "no" or "false" or "0" => "No", "not applicable" or "n/a" or "na" => "Not applicable", _ => null,
    };
}
