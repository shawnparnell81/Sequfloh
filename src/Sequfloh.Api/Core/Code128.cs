using ZXing.OneD;

namespace Sequfloh.Infrastructure.Services;

/// <summary>Code 128 bar patterns for record numbers on printed documents (ZXing.Net encoder, drawn as vector bars so they stay sharp).</summary>
public static class Code128
{
    /// <summary>Module pattern including the quiet zones: true is a bar.</summary>
    public static bool[] Modules(string value) => new Code128Writer().encode(value);

    /// <summary>Draws the barcode right-aligned in a box, with the readable number under it. Returns the height used.</summary>
    public static double Draw(PdfCanvas p, double right, double top, string value, double height = 22, double module = 0.8)
    {
        var bars = Modules(value);
        var x0 = right - bars.Length * module;
        for (var i = 0; i < bars.Length;)
        {
            if (!bars[i]) { i++; continue; }
            var j = i; while (j < bars.Length && bars[j]) j++;
            p.Fill(x0 + i * module, top, (j - i) * module, height, "#000000");
            i = j;
        }
        p.TextAligned(right - 200, top + height + 1, 7.5, 200, value, new TextStyle(Family: "mono"), "right");
        return height + 10;
    }
}
