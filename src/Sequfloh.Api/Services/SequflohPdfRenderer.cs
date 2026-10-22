using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>A block of a SEQUFLOH printed document: heading, label/value fields, wrapped paragraph or table.</summary>
public abstract record PrintBlock;
public sealed record PrintHeading(string Text) : PrintBlock;
public sealed record PrintFields(IReadOnlyList<(string Label, string? Value)> Items) : PrintBlock;
public sealed record PrintParagraph(string Text) : PrintBlock;
public sealed record PrintTable(IReadOnlyList<(string Name, double Weight)> Columns, IReadOnlyList<string?[]> Rows, bool Wrap = true) : PrintBlock;

/// <summary>Header and footer of a SEQUFLOH printed document (organization, module, title, number, revision, status, controlled-copy notice).</summary>
public sealed record PrintHeader(string Organization, string DocumentTitle, string DocumentNumber, string? Revision, string Status, string? EffectiveDate, string? Applicability,
    string ControlledCopyNotice, string PrintedBy, string PrintedAt, string? Watermark);

/// <summary>
/// Controlled printed documents for SEQUFLOH (work instructions, tracking records, audit summaries), drawn with the application's own PDF canvas:
/// organization heading, SEQUFLOH module identification, document identity band, numbered content, repeated table headers,
/// a controlled-copy footer with page X of Y on every page, and a status watermark for anything not released.
/// </summary>
public static class SequflohPdfRenderer
{
    const double W = 612, H = 792, M = 40, Fs = 9;
    const string Brand = "#1257C4", Grey = "#666666", Rule = "#B0B7C3", HeadFill = "#E8EEF8";

    sealed class Ctx(PdfCanvas p, PrintHeader h)
    {
        public readonly PdfCanvas P = p; public readonly PrintHeader Hd = h; public double Y; public int Page = 1;
        public double Inner => W - 2 * M;
        public double Bottom => H - M - 30;
        public Action NewPage = () => { };
        public void Need(double height) { if (Y + height > Bottom) NewPage(); }
    }

    public static byte[] Render(PrintHeader header, IEnumerable<PrintBlock> blocks)
    {
        var pdf = new PdfCanvas(W, H);
        var c = new Ctx(pdf, header);
        c.NewPage = () => { pdf.AddPage(W, H); c.Page++; Header(c); };
        Header(c);
        foreach (var b in blocks)
            switch (b)
            {
                case PrintHeading x: c.Need(34); c.Y += 4; pdf.Fill(M, c.Y - 2, c.Inner, 15, HeadFill); pdf.Text(M + 4, c.Y + 1, 10, x.Text, new TextStyle(true, Colour: Brand)); c.Y += 19; break;
                case PrintFields x: Fields(c, x.Items); break;
                case PrintParagraph x:
                    foreach (var l in PdfCanvas.Wrap(x.Text, Fs, c.Inner)) { c.Need(12); pdf.Text(M, c.Y, Fs, l); c.Y += 12; }
                    c.Y += 4; break;
                case PrintTable x: Table(c, x); break;
            }
        // footers with the final page count
        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            var fy = H - M - 14;
            pdf.Line(M, fy - 5, W - M, fy - 5, 0.6, Rule);
            pdf.TextFit(M, fy, 7.5, c.Inner - 80, header.ControlledCopyNotice);
            pdf.TextFit(M, fy + 10, 7, c.Inner - 80, $"Printed {header.PrintedAt} by {header.PrintedBy} · SEQUFLOH");
            var t = $"Page {i + 1} of {pdf.PageCount}";
            pdf.Text(W - M - PdfCanvas.Width(t, 7.5), fy, 7.5, t);
            if (header.Watermark is { Length: > 0 } wm) pdf.Watermark(wm.ToUpperInvariant(), 64);
        }
        return pdf.ToBytes();
    }

    static void Header(Ctx c)
    {
        var p = c.P; var h = c.Hd;
        var lw = p.Logo(M, M, 22);
        p.TextFit(M + lw + 10, M + 1, 11, c.Inner - lw - 170, h.Organization, bold: true);
        p.Text(M + lw + 10, M + 14, 8, "SEQUFLOH · Enterprise Execution Engine", new TextStyle(Colour: Grey));
        var right = $"{h.DocumentNumber}{(h.Revision is null ? "" : " rev " + h.Revision)}";
        p.TextAligned(W - M - 160, M + 1, 10, 160, right, new TextStyle(true), "right");
        p.TextAligned(W - M - 160, M + 14, 8, 160, h.Status, new TextStyle(Colour: Brand), "right");
        var y = M + 30;
        p.Line(M, y, W - M, y, 1.2, Brand);
        y += 8;
        foreach (var l in PdfCanvas.Wrap(h.DocumentTitle, 13, c.Inner).Take(2)) { p.Text(M, y, 13, l, true); y += 16; }
        var meta = string.Join("   ·   ", new[] { h.EffectiveDate is null ? null : "Effective " + h.EffectiveDate, h.Applicability }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (meta.Length > 0) { p.TextFit(M, y, 8.5, c.Inner, meta); y += 12; }
        c.Y = y + 8;
    }

    static void Fields(Ctx c, IReadOnlyList<(string Label, string? Value)> items)
    {
        var shown = items.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
        if (shown.Count == 0) { c.Need(12); c.P.Text(M, c.Y, Fs, "Not specified.", new TextStyle(Italic: true, Colour: Grey)); c.Y += 14; return; }
        const double lab = 150;
        foreach (var (k, v) in shown)
        {
            var lines = PdfCanvas.Wrap(v!, Fs, c.Inner - lab - 6).ToList();
            c.Need(12 * Math.Min(lines.Count, 3));
            c.P.TextFit(M, c.Y, Fs, lab - 6, k, bold: true);
            foreach (var l in lines) { c.Need(12); c.P.Text(M + lab, c.Y, Fs, l); c.Y += 12; }
        }
        c.Y += 6;
    }

    static void Table(Ctx c, PrintTable t)
    {
        var tw = t.Columns.Sum(x => x.Weight);
        var widths = t.Columns.Select(x => c.Inner * x.Weight / tw).ToArray();
        void Head()
        {
            c.Need(30);
            c.P.Fill(M, c.Y - 2, c.Inner, 14, HeadFill);
            var x = M;
            for (var i = 0; i < widths.Length; i++) { c.P.TextFit(x + 3, c.Y + 1, 8, widths[i] - 6, t.Columns[i].Name, bold: true); x += widths[i]; }
            c.Y += 15;
        }
        Head();
        if (t.Rows.Count == 0) { c.P.Text(M + 3, c.Y, 8.5, "None.", new TextStyle(Italic: true, Colour: Grey)); c.Y += 14; return; }
        foreach (var r in t.Rows)
        {
            var cells = r.Select((v, i) => t.Wrap ? PdfCanvas.Wrap(v ?? "", 8, widths[i] - 6).Take(12).ToList() : [PdfCanvas.Fit(v ?? "", 8, widths[i] - 6)]).ToList();
            var height = Math.Max(1, cells.Max(x => x.Count)) * 10.5 + 3;
            if (c.Y + height > c.Bottom) { c.NewPage(); Head(); }
            var x = M;
            for (var i = 0; i < widths.Length; i++)
            {
                var y = c.Y;
                foreach (var l in cells[i]) { c.P.Text(x + 3, y, 8, l); y += 10.5; }
                x += widths[i];
            }
            c.Y += height;
            c.P.Line(M, c.Y - 2, W - M, c.Y - 2, 0.3, Rule);
        }
        c.Y += 8;
    }
}
