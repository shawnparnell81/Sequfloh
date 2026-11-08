using System.Globalization;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Word and PDF for the monthly report. Word uses the sample's type styles (Aptos Display headings, Aptos body,
/// half-inch margins, Table Grid, and the document-control header). PDF draws the same arrangement.
/// </summary>
public static class MonthlyReportExport
{
    const float PageW = 612, PageH = 792, Margin = 36;
    const string Heading = "#0F4761";

    public static byte[] Docx(ReportIdentity identity, IReadOnlyList<ReportBlock> blocks, IReadOnlyDictionary<string, byte[]> images)
    {
        var media = new List<(string Name, byte[] Bytes)>();
        var rels = new StringBuilder();
        var body = new StringBuilder();
        var pic = 1;
        var next = 2;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ReportTitle t: body.Append(Para("Title", t.Text, "Aptos Display", 56)); break;
                case ReportHeading h:
                    body.Append(Para(h.Level == 2 ? "Heading2" : "Heading3", h.Text, "Aptos Display", h.Level == 2 ? 32 : 28, Heading.TrimStart('#')));
                    break;
                case ReportStatus s: body.Append(StatusPara(s.Value)); break;
                case ReportLine l: body.Append(Para("Normal", l.Text, "Aptos", 24)); break;
                case ReportBullets b:
                    if (b.Items.Count == 0) body.Append(Para("Normal", "", "Aptos", 24));
                    foreach (var item in b.Items) body.Append(Para("ListParagraph", item, "Aptos", 24, bullet: true));
                    break;
                case ReportTable t: body.Append(Table(t)); break;
                case ReportPicture p:
                    var jpeg = images.TryGetValue(p.Slot, out var bytes) ? bytes : MonthlyReportCharts.Note(p.Slot, "No picture.");
                    var name = $"image{pic}.jpeg";
                    media.Add((name, jpeg));
                    var rel = $"rId{next++}";
                    rels.Append($"""<Relationship Id="{rel}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/{name}"/>""");
                    body.Append(Picture(rel, pic, jpeg));
                    pic++;
                    break;
            }
        }
        body.Append("""<w:sectPr><w:headerReference w:type="default" r:id="rIdHeader"/><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720" w:header="720" w:footer="720" w:gutter="0"/><w:cols w:space="720"/><w:docGrid w:linePitch="360"/></w:sectPr>""");
        var document = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
            <w:body>{body}</w:body></w:document>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Put(zip, "[Content_Types].xml", ContentTypes());
            Put(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Put(zip, "word/document.xml", document);
            Put(zip, "word/styles.xml", Styles());
            Put(zip, "word/numbering.xml", Numbering());
            Put(zip, "word/header1.xml", Header(identity));
            Put(zip, "word/_rels/document.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rIdNumbering" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/><Relationship Id="rIdHeader" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>{rels}</Relationships>""");
            foreach (var (file, data) in media)
            {
                var entry = zip.CreateEntry($"word/media/{file}", CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    static string Header(ReportIdentity id) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:p><w:pPr><w:pStyle w:val="Header"/><w:jc w:val="right"/><w:rPr><w:b/></w:rPr></w:pPr>
            <w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve">Page </w:t></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:fldChar w:fldCharType="begin"/></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:fldChar w:fldCharType="end"/></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve"> of </w:t></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:fldChar w:fldCharType="begin"/></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:instrText xml:space="preserve"> NUMPAGES </w:instrText></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:fldChar w:fldCharType="end"/></w:r>
          </w:p>
          <w:p><w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:b/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr><w:t>{Esc(id.Banner)}</w:t></w:r></w:p>
          <w:p><w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr><w:t>{Esc(id.Line)}</w:t></w:r></w:p>
        </w:hdr>
        """;

    static string StatusPara(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Para("Normal", "Status of Department: " + MonthlyReportMath.EmptyNote, "Aptos", 24);
        var mark = value.ToLowerInvariant() switch { "green" => "green", "red" => "red", "yellow" => "yellow", _ => "" };
        var highlighted = mark.Length == 0 ? "" : $"""<w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:sz w:val="24"/><w:szCs w:val="24"/><w:highlight w:val="{mark}"/></w:rPr><w:t>{Esc(value)}</w:t></w:r>""";
        var plain = mark.Length == 0 ? $"""<w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr><w:t>{Esc(value)}</w:t></w:r>""" : highlighted;
        return $"""<w:p><w:pPr><w:pStyle w:val="Normal"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr><w:t xml:space="preserve">Status of Department: </w:t></w:r>{plain}</w:p>""";
    }

    static string Para(string style, string text, string font, int sz, string? color = null, bool bullet = false)
    {
        var num = bullet ? """<w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>""" : "";
        var colour = color is null ? "" : $"""<w:color w:val="{color}"/>""";
        return $"""<w:p><w:pPr><w:pStyle w:val="{style}"/>{num}</w:pPr><w:r><w:rPr><w:rFonts w:ascii="{font}" w:hAnsi="{font}" w:cs="{font}"/>{colour}<w:sz w:val="{sz}"/><w:szCs w:val="{sz}"/></w:rPr><w:t xml:space="preserve">{Esc(text)}</w:t></w:r></w:p>""";
    }

    static string Table(ReportTable t)
    {
        var sb = new StringBuilder();
        sb.Append("""<w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/><w:tblW w:w="5000" w:type="pct"/><w:tblLook w:val="04A0" w:firstRow="1" w:lastRow="0" w:firstColumn="1" w:lastColumn="0" w:noHBand="0" w:noVBand="1"/></w:tblPr>""");
        sb.Append("<w:tr>");
        foreach (var h in t.Headers) sb.Append(Cell(h, bold: true));
        sb.Append("</w:tr>");
        foreach (var row in t.Rows)
        {
            var total = row.Length > 0 && row[0] == "Total";
            sb.Append("<w:tr>");
            for (var i = 0; i < t.Headers.Count; i++) sb.Append(Cell(i < row.Length ? row[i] : "", total));
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl>");
        return sb.ToString();
    }

    static string Cell(string text, bool bold) =>
        $"""<w:tc><w:p><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos"/>{(bold ? "<w:b/>" : "")}<w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr><w:t xml:space="preserve">{Esc(text)}</w:t></w:r></w:p></w:tc>""";

    static string Picture(string rel, int id, byte[] jpeg)
    {
        var (w, h) = JpegSize(jpeg);
        long cx = 6_400_000;
        var cy = (long)(cx * (h / (double)Math.Max(1, w)));
        if (cy > 4_000_000) { cy = 4_000_000; cx = (long)(cy * (w / (double)Math.Max(1, h))); }
        return $"<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"Picture {id}\"/>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
            + $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"image.jpeg\"/><pic:cNvPicPr/></pic:nvPicPr>"
            + $"<pic:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
            + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
            + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    static (int W, int H) JpegSize(byte[] b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return (900, 400);
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            var len = (b[i + 2] << 8) | b[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            if (len < 2) break;
            i += 2 + len;
        }
        return (900, 400);
    }

    static string Styles() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Aptos" w:hAnsi="Aptos" w:cs="Aptos"/><w:sz w:val="24"/><w:szCs w:val="24"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="278" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:spacing w:after="80" w:line="240" w:lineRule="auto"/></w:pPr>
            <w:rPr><w:rFonts w:ascii="Aptos Display" w:hAnsi="Aptos Display" w:cs="Aptos Display"/><w:sz w:val="56"/><w:szCs w:val="56"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:uiPriority w:val="9"/>
            <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr>
            <w:rPr><w:rFonts w:ascii="Aptos Display" w:hAnsi="Aptos Display" w:cs="Aptos Display"/><w:color w:val="0F4761"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="80"/><w:outlineLvl w:val="2"/></w:pPr>
            <w:rPr><w:rFonts w:ascii="Aptos Display" w:hAnsi="Aptos Display" w:cs="Aptos Display"/><w:color w:val="0F4761"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:ind w:left="720"/><w:contextualSpacing/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="Header"><w:name w:val="header"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr></w:style>
          <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/>
            <w:tblPr><w:tblBorders>
              <w:top w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:left w:val="single" w:sz="4" w:space="0" w:color="auto"/>
              <w:bottom w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:right w:val="single" w:sz="4" w:space="0" w:color="auto"/>
              <w:insideH w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="auto"/>
            </w:tblBorders></w:tblPr></w:style>
          <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/>
            <w:tblPr><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        </w:styles>
        """;

    static string Numbering() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:abstractNum w:abstractNumId="0"><w:multiLevelType w:val="hybridMultilevel"/>
            <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="●"/><w:lvlJc w:val="left"/>
              <w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
          </w:abstractNum>
          <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
        </w:numbering>
        """;

    static string ContentTypes() => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Default Extension="jpeg" ContentType="image/jpeg"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
          <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
          <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
        </Types>
        """;

    static string Esc(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

    static void Put(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(xml);
    }

    public static byte[] Pdf(ReportIdentity identity, IReadOnlyList<ReportBlock> blocks, IReadOnlyDictionary<string, byte[]> images)
    {
        var body = Face(false, false);
        var bodyBold = Face(false, true);
        var display = Face(true, false);
        using var probe = new SKFont(body, 12);
        var headerLines = Wrap(identity.Line, probe, PageW - 2 * Margin);
        var headerH = 18 + 18 + headerLines.Count * 16 + 10;
        var contentTop = Margin + headerH;
        var bottom = PageH - Margin;
        var pages = new List<List<(ReportBlock Block, bool Continuation)>>();
        var current = new List<(ReportBlock, bool)>();
        var y = contentTop;
        void Break() { pages.Add(current); current = []; y = contentTop; }
        foreach (var block in blocks)
        {
            var h = Height(block, images, display, body, bodyBold, PageW - 2 * Margin);
            if (y + Math.Min(h, 40) > bottom && current.Count > 0) Break();
            current.Add((block, false));
            y += h;
            if (y > bottom) Break();
        }
        if (current.Count > 0 || pages.Count == 0) pages.Add(current);
        using var ms = new MemoryStream();
        using var doc = SKDocument.CreatePdf(ms);
        for (var i = 0; i < pages.Count; i++)
        {
            using var canvas = doc.BeginPage(PageW, PageH);
            canvas.Clear(SKColors.White);
            DrawHeader(canvas, identity, headerLines, i + 1, pages.Count, body, bodyBold);
            var cy = contentTop;
            foreach (var (block, _) in pages[i])
                cy = Draw(canvas, block, images, cy, bottom, display, body, bodyBold);
            doc.EndPage();
        }
        doc.Close();
        return ms.ToArray();
    }

    static void DrawHeader(SKCanvas canvas, ReportIdentity identity, List<string> lines, int page, int pages, SKTypeface body, SKTypeface boldFace)
    {
        using var bold = new SKFont(boldFace, 12);
        using var regular = new SKFont(body, 12);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var label = $"Page {page} of {pages}";
        canvas.DrawText(label, PageW - Margin - bold.MeasureText(label), Margin + 12, SKTextAlign.Left, bold, paint);
        canvas.DrawText(identity.Banner, Margin, Margin + 30, SKTextAlign.Left, bold, paint);
        var y = Margin + 46;
        foreach (var line in lines)
        {
            canvas.DrawText(line, Margin, y, SKTextAlign.Left, regular, paint);
            y += 16;
        }
    }

    static float Draw(SKCanvas canvas, ReportBlock block, IReadOnlyDictionary<string, byte[]> images, float y, float bottom, SKTypeface display, SKTypeface body, SKTypeface boldFace)
    {
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var width = PageW - 2 * Margin;
        switch (block)
        {
            case ReportTitle t:
                using (var font = new SKFont(display, 28))
                {
                    foreach (var line in Wrap(t.Text, font, width)) { canvas.DrawText(line, Margin, y + 28, SKTextAlign.Left, font, paint); y += 32; }
                }
                return y + 4;
            case ReportHeading h:
                var size = h.Level == 2 ? 16f : 14f;
                y += 8;
                using (var font = new SKFont(display, size))
                using (var hp = new SKPaint { Color = SKColor.Parse(Heading), IsAntialias = true })
                    canvas.DrawText(h.Text, Margin, y + size, SKTextAlign.Left, font, hp);
                return y + size + 6;
            case ReportStatus s:
                using (var font = new SKFont(body, 12))
                {
                    var lead = "Status of Department: ";
                    canvas.DrawText(lead, Margin, y + 12, SKTextAlign.Left, font, paint);
                    var wordText = string.IsNullOrWhiteSpace(s.Value) ? MonthlyReportMath.EmptyNote : s.Value;
                    var x = Margin + font.MeasureText(lead);
                    if (!string.IsNullOrWhiteSpace(s.Value) && Highlight(s.Value) != SKColors.Transparent)
                    {
                        var word = font.MeasureText(wordText);
                        using var mark = new SKPaint { Color = Highlight(s.Value), IsAntialias = true };
                        canvas.DrawRect(x - 1, y + 1, word + 2, 14, mark);
                    }
                    canvas.DrawText(wordText, x, y + 12, SKTextAlign.Left, font, paint);
                }
                return y + 20;
            case ReportLine l:
                using (var font = new SKFont(body, 12))
                {
                    var lines = Wrap(l.Text, font, width);
                    if (lines.Count == 0) lines.Add("");
                    foreach (var line in lines) { canvas.DrawText(line, Margin, y + 12, SKTextAlign.Left, font, paint); y += 16; }
                }
                return y + 4;
            case ReportBullets b:
                using (var font = new SKFont(body, 12))
                {
                    if (b.Items.Count == 0) return y + 4;
                    foreach (var item in b.Items)
                    {
                        var lines = Wrap(item, font, width - 22);
                        if (lines.Count == 0) lines.Add("");
                        canvas.DrawCircle(Margin + 8, y + 8, 2.2f, paint);
                        foreach (var line in lines) { canvas.DrawText(line, Margin + 18, y + 12, SKTextAlign.Left, font, paint); y += 16; }
                    }
                }
                return y + 4;
            case ReportTable t:
                return DrawTable(canvas, t, y, bottom, body, boldFace);
            case ReportPicture p:
                var jpeg = images.TryGetValue(p.Slot, out var bytes) ? bytes : MonthlyReportCharts.Note(p.Slot, "No picture.");
                using (var bitmap = SKBitmap.Decode(jpeg))
                {
                    if (bitmap is null) return y + 8;
                    var drawW = width;
                    var drawH = bitmap.Height * (drawW / Math.Max(1, bitmap.Width));
                    if (drawH > 240) { drawH = 240; drawW = bitmap.Width * (drawH / bitmap.Height); }
                    var dest = new SKRect(Margin, y, Margin + drawW, y + drawH);
                    canvas.DrawBitmap(bitmap, dest);
                    return y + drawH + 10;
                }
            default: return y;
        }
    }

    static float DrawTable(SKCanvas canvas, ReportTable t, float y, float bottom, SKTypeface body, SKTypeface boldFace)
    {
        var cols = Math.Max(1, t.Headers.Count);
        var width = PageW - 2 * Margin;
        var colW = width / cols;
        using var font = new SKFont(body, 11);
        using var bold = new SKFont(boldFace, 11);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var line = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, IsAntialias = true };
        float Row(IReadOnlyList<string> cells, bool header, float top)
        {
            var heights = new float[cols];
            var wrapped = new List<string>[cols];
            var max = 16f;
            for (var i = 0; i < cols; i++)
            {
                var text = i < cells.Count ? cells[i] ?? "" : "";
                wrapped[i] = Wrap(text, header ? bold : font, colW - 8);
                if (wrapped[i].Count == 0) wrapped[i].Add("");
                heights[i] = wrapped[i].Count * 14 + 6;
                max = Math.Max(max, heights[i]);
            }
            for (var i = 0; i < cols; i++)
            {
                var x = Margin + i * colW;
                canvas.DrawRect(x, top, colW, max, line);
                var ty = top + 13;
                foreach (var part in wrapped[i])
                {
                    canvas.DrawText(part, x + 4, ty, SKTextAlign.Left, header ? bold : font, paint);
                    ty += 14;
                }
            }
            return max;
        }
        y += Row(t.Headers, true, y);
        foreach (var row in t.Rows)
        {
            if (y + 22 > bottom) break;
            y += Row(row, row.Length > 0 && row[0] == "Total", y);
        }
        return y + 8;
    }

    static float Height(ReportBlock block, IReadOnlyDictionary<string, byte[]> images, SKTypeface display, SKTypeface body, SKTypeface boldFace, float width)
    {
        switch (block)
        {
            case ReportTitle t:
                using (var font = new SKFont(display, 28)) return Wrap(t.Text, font, width).Count * 32 + 4;
            case ReportHeading h: return (h.Level == 2 ? 16 : 14) + 14;
            case ReportStatus: return 20;
            case ReportLine l:
                using (var font = new SKFont(body, 12)) return Math.Max(1, Wrap(l.Text, font, width).Count) * 16 + 4;
            case ReportBullets b:
                using (var font = new SKFont(body, 12))
                    return b.Items.Sum(item => Math.Max(1, Wrap(item, font, width - 22).Count) * 16) + 4;
            case ReportTable t:
                using (var font = new SKFont(boldFace, 11))
                {
                    var colW = width / Math.Max(1, t.Headers.Count);
                    float rows = Wrap(string.Join(" ", t.Headers), font, colW).Count > 0 ? 22 : 22;
                    rows += t.Rows.Count * 22;
                    return rows + 8;
                }
            case ReportPicture p:
                var jpeg = images.TryGetValue(p.Slot, out var bytes) ? bytes : null;
                if (jpeg is null) return 80;
                var (iw, ih) = JpegSize(jpeg);
                var drawH = ih * (width / Math.Max(1, iw));
                return Math.Min(240, (float)drawH) + 10;
            default: return 8;
        }
    }

    static List<string> Wrap(string? text, SKFont font, float width)
    {
        var lines = new List<string>();
        foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trial = line.Length == 0 ? word : line + " " + word;
                if (font.MeasureText(trial) > width && line.Length > 0) { lines.Add(line); line = word; }
                else line = trial;
            }
            lines.Add(line);
        }
        return lines.Count == 0 ? [""] : lines;
    }

    static SKColor Highlight(string status) => status.ToLowerInvariant() switch
    {
        "green" => new SKColor(0, 255, 0),
        "red" => new SKColor(255, 80, 80),
        "yellow" => new SKColor(255, 255, 0),
        _ => SKColors.Transparent,
    };

    static readonly Dictionary<(bool Display, bool Bold), SKTypeface> Faces = new();

    static SKTypeface Face(bool display, bool bold)
    {
        if (Faces.TryGetValue((display, bold), out var cached)) return cached;
        SKTypeface Keep(SKTypeface face) { Faces[(display, bold)] = face; return face; }
        var style = bold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var family in display ? new[] { "Aptos Display", "Noto Sans Display", "Liberation Sans", "DejaVu Sans" } : new[] { "Aptos", "Noto Sans", "Liberation Sans", "DejaVu Sans" })
        {
            var named = SKTypeface.FromFamilyName(family, style);
            if (Measures(named)) return Keep(named!);
        }
        var file = bold ? "Bold" : "Regular";
        var preferred = display ? new[] { "NotoSansDisplay-" + file + ".ttf", "LiberationSans-" + (bold ? "Bold" : "Regular") + ".ttf" } : new[] { "NotoSans-" + file + ".ttf", "LiberationSans-" + (bold ? "Bold" : "Regular") + ".ttf", "DejaVuSans" + (bold ? "-Bold" : "") + ".ttf" };
        foreach (var root in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.ttf", SearchOption.AllDirectories))
            {
                if (!preferred.Any(n => path.EndsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
                var loaded = SKTypeface.FromFile(path);
                if (Measures(loaded)) return Keep(loaded!);
            }
        }
        return Keep(SKTypeface.FromFamilyName(null, style));
    }

    static bool Measures(SKTypeface? face)
    {
        if (face is null) return false;
        using var font = new SKFont(face, 12);
        return font.MeasureText("M") > 1;
    }
}
