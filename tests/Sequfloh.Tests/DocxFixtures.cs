using System.IO.Compression;
using System.Security;
using System.Text;

namespace Sequfloh.Tests;

/// <summary>Builds Word documents for tests from WordprocessingML written here (no copied files): styles, numbering, header and footer, content controls.</summary>
public sealed class DocxBuilder
{
    public const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"";
    readonly StringBuilder body = new();
    readonly List<(string Id, string Type, string Target, bool External)> rels = [];
    public string? Header { get; set; }
    public string? Footer { get; set; }
    public string? Title { get; set; }
    public int Footnotes { get; set; }
    public bool Macros { get; set; }
    public string SectPr { get; set; } = "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1080\" w:bottom=\"1440\" w:left=\"1080\"/></w:sectPr>";

    public static string X(string s) => SecurityElement.Escape(s);
    public static string Run(string text, string? rpr = null) => $"<w:r>{(rpr is null ? "" : $"<w:rPr>{rpr}</w:rPr>")}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";
    public static string P(string runs, string? ppr = null) => $"<w:p>{(ppr is null ? "" : $"<w:pPr>{ppr}</w:pPr>")}{runs}</w:p>";
    public static string Text(string text, string? style = null, string? rpr = null) => P(Run(text, rpr), style is null ? null : $"<w:pStyle w:val=\"{style}\"/>");
    public static string ControlText(string tag, string alias, string? value = null) =>
        $"<w:sdt><w:sdtPr><w:alias w:val=\"{X(alias)}\"/><w:tag w:val=\"{X(tag)}\"/>{(value is null ? "<w:showingPlcHdr/>" : "")}<w:text/></w:sdtPr><w:sdtContent>{Run(value ?? "Click or tap here to enter text.")}</w:sdtContent></w:sdt>";
    public static string ControlDate(string tag, string alias) =>
        $"<w:sdt><w:sdtPr><w:alias w:val=\"{X(alias)}\"/><w:tag w:val=\"{X(tag)}\"/><w:showingPlcHdr/><w:date><w:dateFormat w:val=\"yyyy-MM-dd\"/></w:date></w:sdtPr><w:sdtContent>{Run("Click or tap to enter a date.")}</w:sdtContent></w:sdt>";
    public static string ControlDropdown(string tag, string alias, params string[] items) =>
        $"<w:sdt><w:sdtPr><w:alias w:val=\"{X(alias)}\"/><w:tag w:val=\"{X(tag)}\"/><w:showingPlcHdr/><w:dropDownList><w:listItem w:displayText=\"Choose an item.\" w:value=\"\"/>{string.Concat(items.Select(i => $"<w:listItem w:displayText=\"{X(i)}\" w:value=\"{X(i)}\"/>"))}</w:dropDownList></w:sdtPr><w:sdtContent>{Run("Choose an item.")}</w:sdtContent></w:sdt>";
    public static string ControlCheck(string tag, bool on = false) =>
        $"<w:sdt><w:sdtPr><w:tag w:val=\"{X(tag)}\"/><w14:checkbox><w14:checked w14:val=\"{(on ? 1 : 0)}\"/></w14:checkbox></w:sdtPr><w:sdtContent>{Run(on ? "☒" : "☐")}</w:sdtContent></w:sdt>";
    public static string LegacyText(string name) =>
        $"<w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"{name}\"/><w:enabled/><w:textInput/></w:ffData></w:fldChar></w:r><w:r><w:instrText xml:space=\"preserve\"> FORMTEXT </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>\u2002\u2002\u2002\u2002\u2002</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
    public static string LegacyCheck(string name) =>
        $"<w:r><w:fldChar w:fldCharType=\"begin\"><w:ffData><w:name w:val=\"{name}\"/><w:enabled/><w:checkBox><w:sizeAuto/><w:default w:val=\"0\"/></w:checkBox></w:ffData></w:fldChar></w:r><w:r><w:instrText xml:space=\"preserve\"> FORMCHECKBOX </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";
    public static string Field(string instr, string shown) =>
        $"<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> {X(instr)} </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>{Run(shown)}<w:r><w:fldChar w:fldCharType=\"end\"/></w:r>";

    /// <summary>A table cell. span = grid columns; vmerge = restart/continue; fill = hex without #.</summary>
    public static string Cell(string content, double widthPt, int span = 1, string? vmerge = null, string? fill = null, bool bold = false) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{(int)(widthPt * 20)}\" w:type=\"dxa\"/>{(span > 1 ? $"<w:gridSpan w:val=\"{span}\"/>" : "")}{(vmerge is null ? "" : vmerge == "restart" ? "<w:vMerge w:val=\"restart\"/>" : "<w:vMerge/>")}{(fill is null ? "" : $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>")}</w:tcPr>"
        + (content.StartsWith("<w:p") ? content : P(content.Length == 0 ? "" : Run(content, bold ? "<w:b/>" : null))) + "</w:tc>";
    public static string Row(string cells, bool header = false, double? heightPt = null) =>
        $"<w:tr>{(header || heightPt is not null ? $"<w:trPr>{(header ? "<w:tblHeader/>" : "")}{(heightPt is { } h ? $"<w:trHeight w:val=\"{(int)(h * 20)}\"/>" : "")}</w:trPr>" : "")}{cells}</w:tr>";
    public static string Table(double[] gridPt, params string[] rows) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/><w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>"
        + $"<w:tblGrid>{string.Concat(gridPt.Select(g => $"<w:gridCol w:w=\"{(int)(g * 20)}\"/>"))}</w:tblGrid>{string.Concat(rows)}</w:tbl>";

    public DocxBuilder Add(string xml) { body.Append(xml); return this; }
    public string Hyperlink(string url, string text) { var id = "rIdL" + (rels.Count + 1); rels.Add((id, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", url, true)); return $"<w:hyperlink r:id=\"{id}\">{Run(text, "<w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/>")}</w:hyperlink>"; }

    public byte[] Build()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); w.Write(xml); }
            var docRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            docRels.Append("<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            docRels.Append("<Relationship Id=\"rIdN\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
            var sect = SectPr;
            var refs = "";
            if (Header is not null) { docRels.Append("<Relationship Id=\"rIdH\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/>"); refs += "<w:headerReference w:type=\"default\" r:id=\"rIdH\"/>"; }
            if (Footer is not null) { docRels.Append("<Relationship Id=\"rIdF\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>"); refs += "<w:footerReference w:type=\"default\" r:id=\"rIdF\"/>"; }
            if (Footnotes > 0) docRels.Append("<Relationship Id=\"rIdFn\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes\" Target=\"footnotes.xml\"/>");
            sect = sect.Replace("<w:sectPr>", "<w:sectPr>" + refs);
            foreach (var (id, type, target, ext) in rels) docRels.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{X(target)}\"{(ext ? " TargetMode=\"External\"" : "")}/>");
            docRels.Append("</Relationships>");
            var main = Macros ? "application/vnd.ms-word.document.macroEnabled.main+xml" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
            Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + $"<Override PartName=\"/word/document.xml\" ContentType=\"{main}\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>"
                + "<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/><Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/><Override PartName=\"/word/footnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
            Put("docProps/core.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{X(Title ?? "")}</dc:title><dc:creator>Quality</dc:creator></cp:coreProperties>");
            Put("word/_rels/document.xml.rels", docRels.ToString());
            Put("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {Ns}><w:body>{body}{sect}</w:body></w:document>");
            Put("word/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles {Ns}><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:sz w:val=\"20\"/></w:rPr></w:rPrDefault></w:docDefaults>"
                + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:sz w:val=\"20\"/></w:rPr></w:style>"
                + "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"1257C4\"/><w:sz w:val=\"36\"/></w:rPr></w:style>"
                + "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"28\"/></w:rPr></w:style>"
                + "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:ind w:left=\"720\"/></w:pPr></w:style></w:styles>");
            Put("word/numbering.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:numbering {Ns}><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"lowerLetter\"/><w:lvlText w:val=\"%1.%2)\"/></w:lvl></w:abstractNum>"
                + "<w:abstractNum w:abstractNumId=\"1\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"•\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"1\"/></w:num></w:numbering>");
            if (Header is not null) Put("word/header1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr {Ns}>{Header}</w:hdr>");
            if (Footer is not null) Put("word/footer1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr {Ns}>{Footer}</w:ftr>");
            if (Footnotes > 0) Put("word/footnotes.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:footnotes {Ns}><w:footnote w:type=\"separator\" w:id=\"-1\"/><w:footnote w:type=\"continuationSeparator\" w:id=\"0\"/>{string.Concat(Enumerable.Range(1, Footnotes).Select(i => $"<w:footnote w:id=\"{i}\">{Text("Note " + i)}</w:footnote>"))}</w:footnotes>");
            if (Macros) using (var s = zip.CreateEntry("word/vbaProject.bin").Open()) s.Write([0xD0, 0xCF, 0x11, 0xE0]);
        }
        return ms.ToArray();
    }

    static string Num(int numId, int level = 0) => $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr>";

    /// <summary>
    /// A first article inspection report as a quality team would write it in Word: title, controls in the header block, numbered instructions,
    /// placeholders, checkbox symbols, a label/value table with a merged heading, a repeating measurement table with a row formula and a
    /// totals formula, a signature table, a landscape appendix section, page numbers in the footer.
    /// </summary>
    public static byte[] FirstArticleReport(bool macros = false)
    {
        var b = new DocxBuilder { Title = "First Article Inspection Report", Footnotes = 1, Macros = macros };
        b.Header = P(Run("QF-0412 First Article Inspection") + "<w:r><w:tab/></w:r>" + Run("Rev C"));
        b.Footer = P(Run("Page ") + Field("PAGE", "1") + Run(" of ") + Field("NUMPAGES", "2"), "<w:jc w:val=\"center\"/>");
        b.Add(Text("First Article Inspection Report", "Title"));
        b.Add(P(Run("Supplier: ") + ControlText("supplier_name", "Supplier") + Run("   Inspection date: ") + ControlDate("inspection_date", "Inspection date")));
        b.Add(P(Run("Part number: ________________   Revision: ______")));
        b.Add(P(Run("Reason for inspection: ") + ControlDropdown("reason", "Reason", "New part", "Design change", "Process change", "Supplier change")));
        b.Add(Text("Instructions", "Heading1"));
        b.Add(P(Run("Measure every characteristic on the ballooned drawing."), "<w:pStyle w:val=\"ListParagraph\"/>" + Num(1)));
        b.Add(P(Run("Record the measured value and the gauge used."), "<w:pStyle w:val=\"ListParagraph\"/>" + Num(1, 1)));
        b.Add(P(Run("Attach the CMM report. See ") + b.Hyperlink("https://example.com/fai-guide", "the FAI guide") + Run(" and ") + b.Hyperlink("javascript:alert(1)", "this link") + Run("."), "<w:pStyle w:val=\"ListParagraph\"/>" + Num(1)));
        b.Add(P(Run("Highlighted: characteristics marked CC are critical.", "<w:highlight w:val=\"yellow\"/><w:b/>")));
        b.Add(Text("Part details", "Heading1"));
        b.Add(Text("Part details"));
        b.Add(Table([160, 120, 110, 110],
            Row(Cell("Part identification", 500, 4, fill: "D9E2F3", bold: true)),
            Row(Cell("Drawing number", 160) + Cell("", 120) + Cell("Lot quantity", 110) + Cell("", 110)),
            Row(Cell("Material", 160) + Cell(P(ControlText("material", "Material")), 120) + Cell("Sample size", 110) + Cell(P(LegacyText("SampleSize")), 110)),
            Row(Cell("Accepted lots", 160) + Cell("", 120) + Cell("Rejected lots", 110) + Cell("", 110)),
            Row(Cell("Total lots", 160) + Cell(P(Field("=B4+D4", "0")), 120) + Cell("Customer approval required", 110) + Cell(P(ControlCheck("customer_approval")), 110))));
        b.Add(Text("Measurements", "Heading1"));
        b.Add(Text("Dimensional results"));
        b.Add(Table([40, 160, 90, 90, 120],
            Row(Cell("No.", 40, bold: true) + Cell("Characteristic", 160, bold: true) + Cell("Nominal", 90, bold: true) + Cell("Measured", 90, bold: true) + Cell("Deviation", 120, bold: true), header: true),
            Row(Cell("1", 40) + Cell("", 160) + Cell("", 90) + Cell("", 90) + Cell(P(Field("=D2-C2", "0")), 120)),
            Row(Cell("2", 40) + Cell("", 160) + Cell("", 90) + Cell("", 90) + Cell(P(Field("=D3-C3", "0")), 120)),
            Row(Cell("3", 40) + Cell("", 160) + Cell("", 90) + Cell("", 90) + Cell(P(Field("=D4-C4", "0")), 120)),
            Row(Cell("Total", 200, 2, bold: true) + Cell("", 90) + Cell(P(Field("=SUM(ABOVE)", "0")), 90) + Cell(P(Field("=SUM(ABOVE)", "0")), 120))));
        b.Add(P(Run("Result: ☐ Accept   ☐ Reject   ☐ Accept with deviation")));
        b.Add(P(Run("Material certificate attached ") + LegacyCheck("CertAttached")));
        b.Add(Text("Approval", "Heading1"));
        b.Add(Table([70, 120, 150, 50, 110],
            Row(Cell("Sign-off", 70, vmerge: "restart", fill: "F2F2F2", bold: true) + Cell("Inspected by", 120) + Cell("", 150) + Cell("Date", 50) + Cell("", 110), heightPt: 28),
            Row(Cell("", 70, vmerge: "continue") + Cell("Approved by", 120) + Cell("", 150) + Cell("Date", 50) + Cell("", 110), heightPt: 28)));
        b.Add(P("<w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"1\" name=\"Logo\" descr=\"Company logo\"/></wp:inline></w:drawing></w:r>"));
        b.Add(P(Run("Notes are on the next page."), "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1080\" w:bottom=\"1440\" w:left=\"1080\"/></w:sectPr>"));
        b.Add(Text("Appendix: deviation notes", "Heading1"));
        b.Add(P(Run("Deviation notes: ") + Run("[Describe any deviation and the containment taken]")));
        b.SectPr = "<w:sectPr><w:pgSz w:w=\"15840\" w:h=\"12240\" w:orient=\"landscape\"/><w:pgMar w:top=\"1080\" w:right=\"1440\" w:bottom=\"1080\" w:left=\"1440\"/></w:sectPr>";
        return b.Build();
    }
}
