namespace Sequfloh.Imports;

/// <summary>The look of one spreadsheet cell, read from the file and sent to the browser as is.</summary>
public sealed class CellStyle
{
    /// <summary>sans, serif or mono (closest standard face of the source font).</summary>
    public string? Font { get; set; }
    /// <summary>Source font name.</summary>
    public string? FontName { get; set; }
    public double? Size { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string? Colour { get; set; }
    public string? Fill { get; set; }
    /// <summary>left, center, right.</summary>
    public string? Align { get; set; }
    /// <summary>top, middle, bottom.</summary>
    public string? VAlign { get; set; }
    public bool Wrap { get; set; }
    /// <summary>Border styles: thin, medium, thick, dashed, dotted, double, or null for none.</summary>
    public string? BorderTop { get; set; }
    public string? BorderRight { get; set; }
    public string? BorderBottom { get; set; }
    public string? BorderLeft { get; set; }
    public string? BorderColour { get; set; }
    /// <summary>Number format from the source (e.g. "0.000", "yyyy-mm-dd").</summary>
    public string? NumberFormat { get; set; }
}

/// <summary>Header or footer text of a worksheet.</summary>
public sealed class BandText
{
    public string? Left { get; set; }
    public string? Center { get; set; }
    public string? Right { get; set; }
}
