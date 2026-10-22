using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Imports;

public sealed record WStyle(bool Bold, bool Italic, bool Underline, string? Colour, string? Highlight, double? Size, string? Font);
/// <summary>A content control or legacy form field. Type: text, richtext, date, dropdown, combobox, checkbox, picture, group.</summary>
public sealed class WControl
{
    public string Type { get; set; } = "text";
    public string? Tag { get; set; }
    public string? Alias { get; set; }
    public List<string> Items { get; set; } = [];
    public bool Checked { get; set; }
    public string? DateFormat { get; set; }
    public string Text { get; set; } = "";
    public bool ShowingPlaceholder { get; set; }
    public bool Legacy { get; set; }
}
/// <summary>An inline piece of a paragraph. Kind: text, control, tab, break, page (page number), pages (page count), formula (a field code such as =SUM(ABOVE)).</summary>
public sealed record WSeg(string Kind, string Text, WStyle? Style, WControl? Control = null, string? Link = null);
public sealed class WPara
{
    public List<WSeg> Segs { get; } = [];
    public string? StyleId { get; set; }
    public string? StyleName { get; set; }
    public int? Heading { get; set; }
    public string? Align { get; set; }
    public double Indent { get; set; }
    public double Before { get; set; }
    public double After { get; set; }
    public string? ListLabel { get; set; }
    public int ListLevel { get; set; }
    public bool PageBreakBefore { get; set; }
    public WStyle? Style { get; set; }
    /// <summary>Position of the paragraph among the paragraphs of the document body, used to write an edit back; null for headers and footers.</summary>
    public int? Ref { get; set; }
    /// <summary>Plain text paragraphs can be edited in the app; paragraphs with fields or controls keep their content as in the file.</summary>
    public bool Editable => Ref is not null && Segs.All(s => s.Kind is "text" or "tab" or "break");
    public string Text => string.Concat(Segs.Where(s => s.Kind is "text" or "tab").Select(s => s.Kind == "tab" ? " " : s.Text));
}
public sealed class WCell
{
    public List<WPara> Paras { get; } = [];
    public List<WTable> Nested { get; } = [];
    public int GridSpan { get; set; } = 1;
    /// <summary>restart, continue or null.</summary>
    public string? VMerge { get; set; }
    public double? Width { get; set; }
    public string? Shading { get; set; }
    public string? BorderTop { get; set; }
    public string? BorderBottom { get; set; }
    public string? BorderLeft { get; set; }
    public string? BorderRight { get; set; }
    public string? VAlign { get; set; }
    public string Text => string.Join("\n", Paras.Select(p => p.Text)).Trim();
    public IEnumerable<WSeg> Segs => Paras.SelectMany(p => p.Segs);
}
public sealed class WRow { public List<WCell> Cells { get; } = []; public double? Height { get; set; } public bool Header { get; set; } }
public sealed class WTable { public List<double> Grid { get; } = []; public List<WRow> Rows { get; } = []; public string? Borders { get; set; } }
public sealed class WSection { public string Orientation { get; set; } = "portrait"; public double WidthPt { get; set; } = 612; public double HeightPt { get; set; } = 792; public double[] MarginsPt { get; set; } = [72, 72, 72, 72]; }
/// <summary>Kind: p, table, section (a section break; <see cref="Section"/> describes the section that ends here).</summary>
public sealed class WBlock { public string Kind { get; init; } = "p"; public WPara? P { get; init; } public WTable? T { get; init; } public WSection? Section { get; init; } }

public sealed class DocModel
{
    public List<WBlock> Blocks { get; } = [];
    public WSection FinalSection { get; set; } = new();
    public List<WPara> Header { get; } = [];
    public List<WPara> Footer { get; } = [];
    public int Images { get; set; }
    public int Footnotes { get; set; }
    public int Endnotes { get; set; }
    public int Bookmarks { get; set; }
    public int Hyperlinks { get; set; }
    public int TextBoxes { get; set; }
    public int Embedded { get; set; }
    public int Comments { get; set; }
    public int TrackedChanges { get; set; }
    public List<string> SignatureLines { get; } = [];
    public HashSet<string> Fonts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Security { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasMacros { get; set; }
    public string? Title { get; set; }
    public IEnumerable<WTable> Tables => Blocks.Where(b => b.T is not null).Select(b => b.T!);
    public IEnumerable<WControl> Controls => Blocks.SelectMany(b => b.P is not null ? b.P.Segs : b.T is not null ? AllSegs(b.T) : []).Where(s => s.Control is not null).Select(s => s.Control!);
    static IEnumerable<WSeg> AllSegs(WTable t) => t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Segs.Concat(c.Nested.SelectMany(AllSegs)));
}

/// <summary>
/// Reads a Word document (DOCX) into a neutral content and layout model: paragraphs with styles, headings, list numbering, runs (bold, italic,
/// underline, colour, highlight, size, font), tables (grid widths, row heights, header rows, merged cells, borders, shading), content controls and
/// legacy form fields, field codes (page numbers, table formulas), section breaks with orientation and size, headers and footers. Nothing is run:
/// macros, embedded objects and external resources are only counted and reported; XML is read with DTDs prohibited.
/// </summary>
public static class DocxReader
{
    public const string ToolVersion = "docx-reader/1";
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace CP = "http://purl.org/dc/elements/1.1/";

    sealed class Ctx
    {
        public Dictionary<string, (string Name, string? BasedOn, WStyle Run, string? Align, int? Outline)> Styles = [];
        public Dictionary<string, Dictionary<int, (string Fmt, string Text, int Start)>> Numbering = [];
        public Dictionary<string, int[]> Counters = [];
        public Dictionary<string, (string Target, bool External)> Rels = [];
        public DocModel Model = new();
        public Dictionary<XElement, int> ParaIndex = [];
    }

    static XDocument Load(ZipArchive zip, string path)
    {
        var e = zip.GetEntry(path) ?? throw new AppException($"The document part {path} is missing.");
        using var s = e.Open();
        using var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 200_000_000 });
        return XDocument.Load(xr);
    }
    static XDocument? TryLoad(ZipArchive zip, string path) => zip.GetEntry(path) is null ? null : Load(zip, path);

    public static DocModel Read(Stream input)
    {
        var ms = new MemoryStream(); input.CopyTo(ms); ms.Position = 0;
        ZipArchive zip;
        try { zip = new ZipArchive(ms, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new AppException("The document is damaged or is not a Word document."); }
        using (zip)
        {
            var ctx = new Ctx();
            var m = ctx.Model;
            m.HasMacros = zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
            if (m.HasMacros) m.Security.Add("The document contains macro code. It is kept in the original file and is never run.");
            m.Embedded = zip.Entries.Count(e => e.FullName.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase));
            if (m.Embedded > 0) m.Security.Add($"{m.Embedded} embedded object(s) are treated as untrusted and are not opened.");
            if (zip.Entries.Any(e => e.FullName.Contains("activeX", StringComparison.OrdinalIgnoreCase))) m.Security.Add("ActiveX controls are present and are never run.");
            if (TryLoad(zip, "word/_rels/document.xml.rels") is { } rels)
                foreach (var r in rels.Root!.Elements(PR + "Relationship"))
                {
                    var external = (string?)r.Attribute("TargetMode") == "External";
                    ctx.Rels[(string)r.Attribute("Id")!] = ((string)r.Attribute("Target")!, external);
                    var type = (string?)r.Attribute("Type") ?? "";
                    if (external && !type.EndsWith("/hyperlink")) m.Security.Add($"External resource {(string)r.Attribute("Target")!} ({type.Split('/').Last()}) is not retrieved.");
                }
            if (TryLoad(zip, "docProps/core.xml") is { } core) m.Title = core.Descendants(CP + "title").FirstOrDefault()?.Value is { Length: > 0 } t ? t.Trim() : null;
            ReadStyles(zip, ctx);
            ReadNumbering(zip, ctx);
            var doc = Load(zip, "word/document.xml");
            var index = 0;
            foreach (var p in doc.Descendants(W + "p")) ctx.ParaIndex[p] = index++;
            var body = doc.Root!.Element(W + "body") ?? throw new AppException("The document has no body.");
            foreach (var el in body.Elements()) Block(el, ctx, m.Blocks);
            if (body.Element(W + "sectPr") is { } last) m.FinalSection = Section(last);
            // header and footer of the final section (default type)
            var sp = body.Element(W + "sectPr");
            foreach (var (refName, target) in new[] { ("headerReference", m.Header), ("footerReference", m.Footer) })
            {
                var rid = sp?.Elements(W + refName).FirstOrDefault(x => (string?)x.Attribute(W + "type") is null or "default")?.Attribute(R + "id")?.Value;
                if (rid is not null && ctx.Rels.TryGetValue(rid, out var rel) && !rel.External && TryLoad(zip, "word/" + rel.Target.TrimStart('/').Replace("word/", "")) is { } part)
                    foreach (var p in part.Root!.Descendants(W + "p")) target.Add(Para(p, ctx));
            }
            m.Footnotes = Math.Max(0, (TryLoad(zip, "word/footnotes.xml")?.Root?.Elements(W + "footnote").Count() ?? 0) - 2);
            m.Endnotes = Math.Max(0, (TryLoad(zip, "word/endnotes.xml")?.Root?.Elements(W + "endnote").Count() ?? 0) - 2);
            m.Comments = TryLoad(zip, "word/comments.xml")?.Root?.Elements(W + "comment").Count() ?? 0;
            m.Bookmarks = doc.Descendants(W + "bookmarkStart").Count(b => (string?)b.Attribute(W + "name") is { } n && !n.StartsWith('_'));
            m.Images = doc.Descendants().Count(e => e.Name.LocalName is "drawing" or "pict" && !e.Descendants().Any(d => d.Name.LocalName == "txbxContent"));
            m.TextBoxes = doc.Descendants().Count(e => e.Name.LocalName == "txbxContent");
            m.TrackedChanges = doc.Descendants().Count(e => e.Name == W + "ins" || e.Name == W + "del");
            foreach (var sl in doc.Descendants().Where(e => e.Name.LocalName == "signatureline"))
                m.SignatureLines.Add((string?)sl.Attributes().FirstOrDefault(a => a.Name.LocalName == "suggestedsigner")?.Value ?? "Signature");
            if (m.TrackedChanges > 0) m.Warnings.Add($"{m.TrackedChanges} tracked change(s): inserted text is read as accepted and deleted text is left out.");
            return m;
        }
    }

    static void ReadStyles(ZipArchive zip, Ctx ctx)
    {
        if (TryLoad(zip, "word/styles.xml") is not { } st) return;
        foreach (var s in st.Root!.Elements(W + "style"))
        {
            var id = (string?)s.Attribute(W + "styleId"); if (id is null) continue;
            var name = (string?)s.Element(W + "name")?.Attribute(W + "val") ?? id;
            var rpr = s.Element(W + "rPr");
            var ppr = s.Element(W + "pPr");
            int? outline = int.TryParse((string?)ppr?.Element(W + "outlineLvl")?.Attribute(W + "val"), out var o) ? o : null;
            ctx.Styles[id] = (name, (string?)s.Element(W + "basedOn")?.Attribute(W + "val"), RunStyle(rpr, null, ctx), (string?)ppr?.Element(W + "jc")?.Attribute(W + "val"), outline);
        }
    }

    static (string Name, WStyle Run, string? Align, int? Outline) StyleOf(string? id, Ctx ctx)
    {
        WStyle run = new(false, false, false, null, null, null, null); string name = id ?? "Normal"; string? align = null; int? outline = null;
        var chain = new List<string>();
        for (var cur = id; cur is not null && ctx.Styles.TryGetValue(cur, out var s) && chain.Count < 10; cur = s.BasedOn) chain.Add(cur);
        if (chain.Count > 0) name = ctx.Styles[chain[0]].Name;
        foreach (var c in Enumerable.Reverse(chain))
        {
            var s = ctx.Styles[c];
            run = Merge(run, s.Run);
            align = s.Align ?? align; outline = s.Outline ?? outline;
        }
        return (name, run, align, outline);
    }

    static WStyle Merge(WStyle a, WStyle b) => new(a.Bold || b.Bold, a.Italic || b.Italic, a.Underline || b.Underline, b.Colour ?? a.Colour, b.Highlight ?? a.Highlight, b.Size ?? a.Size, b.Font ?? a.Font);

    static void ReadNumbering(ZipArchive zip, Ctx ctx)
    {
        if (TryLoad(zip, "word/numbering.xml") is not { } nx) return;
        var abs = new Dictionary<string, Dictionary<int, (string, string, int)>>();
        foreach (var a in nx.Root!.Elements(W + "abstractNum"))
        {
            var levels = new Dictionary<int, (string, string, int)>();
            foreach (var l in a.Elements(W + "lvl"))
                levels[int.Parse((string)l.Attribute(W + "ilvl")!, CultureInfo.InvariantCulture)] = ((string?)l.Element(W + "numFmt")?.Attribute(W + "val") ?? "decimal",
                    (string?)l.Element(W + "lvlText")?.Attribute(W + "val") ?? "", int.TryParse((string?)l.Element(W + "start")?.Attribute(W + "val"), out var st) ? st : 1);
            abs[(string)a.Attribute(W + "abstractNumId")!] = levels;
        }
        foreach (var n in nx.Root.Elements(W + "num"))
            if ((string?)n.Element(W + "abstractNumId")?.Attribute(W + "val") is { } aid && abs.TryGetValue(aid, out var lv)) ctx.Numbering[(string)n.Attribute(W + "numId")!] = lv;
    }

    static string? ListLabel(string numId, int level, Ctx ctx)
    {
        if (!ctx.Numbering.TryGetValue(numId, out var levels) || !levels.TryGetValue(level, out var lv)) return null;
        if (!ctx.Counters.TryGetValue(numId, out var counts)) ctx.Counters[numId] = counts = new int[9];
        counts[level] = counts[level] == 0 ? lv.Start : counts[level] + 1;
        for (var i = level + 1; i < 9; i++) counts[i] = 0;
        if (lv.Fmt == "bullet") return "•";
        if (lv.Fmt == "none") return "";
        return Regex.Replace(lv.Text, "%(\\d)", mm =>
        {
            var li = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            var v = li >= 0 && li < 9 ? (counts[li] == 0 && levels.TryGetValue(li, out var pl) ? pl.Start : counts[li]) : 1;
            var fmt = levels.TryGetValue(li, out var x) ? x.Fmt : "decimal";
            return Format(v, fmt);
        });
    }

    static string Format(int v, string fmt) => fmt switch
    {
        "lowerLetter" => ((char)('a' + (v - 1) % 26)).ToString(),
        "upperLetter" => ((char)('A' + (v - 1) % 26)).ToString(),
        "lowerRoman" => Roman(v).ToLowerInvariant(),
        "upperRoman" => Roman(v),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };
    static string Roman(int n) { var sb = new StringBuilder(); foreach (var (v, s) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") }) while (n >= v) { sb.Append(s); n -= v; } return sb.ToString(); }

    static double Tw(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d / 20.0 : 0;

    static WSection Section(XElement sp)
    {
        var pg = sp.Element(W + "pgSz"); var mar = sp.Element(W + "pgMar");
        var s = new WSection { WidthPt = Tw((string?)pg?.Attribute(W + "w")) is > 0 and var w ? w : 612, HeightPt = Tw((string?)pg?.Attribute(W + "h")) is > 0 and var h ? h : 792 };
        s.Orientation = (string?)pg?.Attribute(W + "orient") == "landscape" || s.WidthPt > s.HeightPt ? "landscape" : "portrait";
        if (mar is not null) s.MarginsPt = [Tw((string?)mar.Attribute(W + "left")), Tw((string?)mar.Attribute(W + "right")), Tw((string?)mar.Attribute(W + "top")), Tw((string?)mar.Attribute(W + "bottom"))];
        return s;
    }

    static void Block(XElement el, Ctx ctx, List<WBlock> into)
    {
        if (el.Name == W + "p")
        {
            var p = Para(el, ctx);
            into.Add(new WBlock { Kind = "p", P = p });
            if (el.Element(W + "pPr")?.Element(W + "sectPr") is { } sp) into.Add(new WBlock { Kind = "section", Section = Section(sp) });
        }
        else if (el.Name == W + "tbl") into.Add(new WBlock { Kind = "table", T = Table(el, ctx) });
        else if (el.Name == W + "sdt")
        {
            var content = el.Element(W + "sdtContent");
            var pr = el.Element(W + "sdtPr");
            var paras = content?.Elements(W + "p").ToList() ?? [];
            if (pr is not null && paras.Count == 1 && content!.Elements().Count() == 1 && IsFieldControl(pr))
            {
                // a block-level control around one paragraph: the paragraph's text is the control's value
                var c = Control(pr, paras[0]);
                var p = new WPara { Style = new(false, false, false, null, null, null, null) };
                p.Segs.Add(new WSeg("control", c.Text, null, c));
                into.Add(new WBlock { Kind = "p", P = p });
            }
            else if (content is not null) foreach (var x in content.Elements()) Block(x, ctx, into);
        }
        else if (el.Name == W + "customXml" || el.Name == W + "ins" || el.Name == W + "smartTag") foreach (var x in el.Elements()) Block(x, ctx, into);
    }

    /// <summary>Building-block galleries (cover pages, tables of contents) and group controls are layout, not fields.</summary>
    static bool IsFieldControl(XElement pr) => !pr.Elements().Any(e => e.Name.LocalName is "docPartObj" or "docPartList" or "group");

    static WControl Control(XElement pr, XElement content)
    {
        var c = new WControl { Tag = (string?)pr.Element(W + "tag")?.Attribute(W + "val"), Alias = (string?)pr.Element(W + "alias")?.Attribute(W + "val") };
        c.ShowingPlaceholder = pr.Element(W + "showingPlcHdr") is not null;
        if (pr.Element(W14 + "checkbox") is { } cb) { c.Type = "checkbox"; c.Checked = (string?)cb.Element(W14 + "checked")?.Attribute(W14 + "val") is "1" or "true"; }
        else if (pr.Element(W + "date") is { } d) { c.Type = "date"; c.DateFormat = (string?)d.Element(W + "dateFormat")?.Attribute(W + "val"); }
        else if (pr.Element(W + "dropDownList") is { } dl) { c.Type = "dropdown"; c.Items = dl.Elements(W + "listItem").Select(i => (string?)i.Attribute(W + "displayText") ?? (string?)i.Attribute(W + "value") ?? "").Where(x => x.Length > 0).ToList(); }
        else if (pr.Element(W + "comboBox") is { } cbx) { c.Type = "combobox"; c.Items = cbx.Elements(W + "listItem").Select(i => (string?)i.Attribute(W + "displayText") ?? (string?)i.Attribute(W + "value") ?? "").Where(x => x.Length > 0).ToList(); }
        else if (pr.Element(W + "picture") is not null) c.Type = "picture";
        else if (pr.Element(W + "richText") is not null) c.Type = "richtext";
        else c.Type = "text";
        c.Text = string.Concat(content.Descendants(W + "t").Select(t => t.Value)).Trim();
        if (c.Type == "checkbox") c.Text = c.Checked ? "☒" : "☐";
        return c;
    }

    static WStyle RunStyle(XElement? rpr, WStyle? baseStyle, Ctx ctx)
    {
        var b = baseStyle ?? new WStyle(false, false, false, null, null, null, null);
        if (rpr is null) return b;
        bool On(string n, bool cur) => rpr.Element(W + n) is { } e ? (string?)e.Attribute(W + "val") is not ("0" or "false" or "none") : cur;
        var colour = (string?)rpr.Element(W + "color")?.Attribute(W + "val");
        var size = double.TryParse((string?)rpr.Element(W + "sz")?.Attribute(W + "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sz) ? sz / 2 : (double?)null;
        var font = (string?)rpr.Element(W + "rFonts")?.Attribute(W + "ascii") ?? (string?)rpr.Element(W + "rFonts")?.Attribute(W + "hAnsi");
        if (font is not null) ctx.Model.Fonts.Add(font);
        var hl = (string?)rpr.Element(W + "highlight")?.Attribute(W + "val") ?? ((string?)rpr.Element(W + "shd")?.Attribute(W + "fill") is { } f && f != "auto" ? "#" + f : null);
        return new WStyle(On("b", b.Bold), On("i", b.Italic), rpr.Element(W + "u") is { } u ? (string?)u.Attribute(W + "val") is not "none" : b.Underline,
            colour is null or "auto" ? b.Colour : "#" + colour.ToUpperInvariant(), hl ?? b.Highlight, size ?? b.Size, font ?? b.Font);
    }

    static WPara Para(XElement p, Ctx ctx)
    {
        var ppr = p.Element(W + "pPr");
        var para = new WPara { StyleId = (string?)ppr?.Element(W + "pStyle")?.Attribute(W + "val") };
        var (name, baseRun, align, outline) = StyleOf(para.StyleId, ctx);
        para.StyleName = name;
        if (ctx.ParaIndex.TryGetValue(p, out var at)) para.Ref = at;
        var hm = Regex.Match(name, @"^heading\s*(\d)$", RegexOptions.IgnoreCase);
        para.Heading = hm.Success ? int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture) : name.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 0 : outline is { } ol && ol < 9 ? ol + 1 : null;
        if (int.TryParse((string?)ppr?.Element(W + "outlineLvl")?.Attribute(W + "val"), out var pol) && pol < 9) para.Heading = pol + 1;
        para.Align = (string?)ppr?.Element(W + "jc")?.Attribute(W + "val") ?? align;
        para.Align = para.Align switch { "center" => "center", "right" or "end" => "right", null => null, _ => "left" };
        para.Indent = Tw((string?)ppr?.Element(W + "ind")?.Attribute(W + "left") ?? (string?)ppr?.Element(W + "ind")?.Attribute(W + "start"));
        para.Before = Tw((string?)ppr?.Element(W + "spacing")?.Attribute(W + "before"));
        para.After = Tw((string?)ppr?.Element(W + "spacing")?.Attribute(W + "after"));
        para.PageBreakBefore = ppr?.Element(W + "pageBreakBefore") is not null;
        var paraRun = RunStyle(ppr?.Element(W + "rPr"), baseRun, ctx);
        para.Style = baseRun;
        if (ppr?.Element(W + "numPr") is { } np && (string?)np.Element(W + "numId")?.Attribute(W + "val") is { } numId && numId != "0")
        {
            para.ListLevel = int.TryParse((string?)np.Element(W + "ilvl")?.Attribute(W + "val"), out var il) ? il : 0;
            para.ListLabel = ListLabel(numId, para.ListLevel, ctx);
        }
        var field = new FieldState();
        Inline(p.Elements(), para, baseRun, ctx, field, null);
        return para;
    }

    sealed class FieldState { public int Depth; public StringBuilder Instr = new(); public bool InResult; public XElement? FfData; public List<WSeg> Result = []; }

    static void Inline(IEnumerable<XElement> els, WPara para, WStyle baseRun, Ctx ctx, FieldState f, string? link)
    {
        foreach (var el in els)
        {
            var n = el.Name.LocalName;
            if (el.Name.Namespace != W && n != "AlternateContent") continue;
            switch (n)
            {
                case "r": Run(el, para, baseRun, ctx, f, link); break;
                case "hyperlink":
                    ctx.Model.Hyperlinks++;
                    var rid = (string?)el.Attribute(R + "id");
                    var target = rid is not null && ctx.Rels.TryGetValue(rid, out var rel) ? rel.Target : (string?)el.Attribute(W + "anchor") is { } a ? "#" + a : null;
                    if (target is not null && Regex.IsMatch(target, @"^\s*(javascript|vbscript|data|file):", RegexOptions.IgnoreCase)) { ctx.Model.Security.Add($"Unsafe hyperlink removed: {target}"); target = null; }
                    Inline(el.Elements(), para, baseRun, ctx, f, target);
                    break;
                case "sdt":
                    var pr = el.Element(W + "sdtPr"); var content = el.Element(W + "sdtContent");
                    if (pr is not null && content is not null && IsFieldControl(pr)) { var c = Control(pr, content); para.Segs.Add(new WSeg("control", c.Text, baseRun, c)); }
                    else if (content is not null) Inline(content.Elements(), para, baseRun, ctx, f, link);
                    break;
                case "fldSimple":
                    var instr = ((string?)el.Attribute(W + "instr") ?? "").Trim();
                    var result = string.Concat(el.Descendants(W + "t").Select(t => t.Value));
                    FieldSeg(instr, result, null, para, baseRun);
                    break;
                case "ins" or "smartTag" or "customXml" or "bdo" or "dir": Inline(el.Elements(), para, baseRun, ctx, f, link); break;
                case "AlternateContent":
                    var choice = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Fallback") ?? el.Elements().FirstOrDefault();
                    if (choice is not null) Inline(choice.Elements(), para, baseRun, ctx, f, link);
                    break;
            }
        }
    }

    static void Run(XElement r, WPara para, WStyle baseRun, Ctx ctx, FieldState f, string? link)
    {
        var rs = RunStyle(r.Element(W + "rPr"), (string?)r.Element(W + "rPr")?.Element(W + "rStyle")?.Attribute(W + "val") is { } cs ? Merge(baseRun, StyleOf(cs, ctx).Run) : baseRun, ctx);
        foreach (var x in r.Elements())
        {
            switch (x.Name.LocalName)
            {
                case "fldChar":
                    var t = (string?)x.Attribute(W + "fldCharType");
                    if (t == "begin") { if (f.Depth++ == 0) { f.Instr.Clear(); f.InResult = false; f.FfData = x.Element(W + "ffData"); f.Result.Clear(); } }
                    else if (t == "separate" && f.Depth == 1) f.InResult = true;
                    else if (t == "end" && --f.Depth == 0)
                    {
                        FieldSeg(f.Instr.ToString().Trim(), string.Concat(f.Result.Select(s => s.Text)), f.FfData, para, rs);
                        f.InResult = false;
                    }
                    break;
                case "instrText": if (f.Depth >= 1) f.Instr.Append(x.Value); break;
                case "t" or "delText":
                    if (x.Name.LocalName == "delText") break;
                    if (f.Depth > 0) { if (f.InResult) f.Result.Add(new WSeg("text", x.Value, rs)); break; }
                    para.Segs.Add(new WSeg("text", x.Value, rs, null, link));
                    break;
                case "tab" or "ptab": if (f.Depth == 0) para.Segs.Add(new WSeg("tab", "\t", rs)); break;
                case "br" or "cr":
                    if ((string?)x.Attribute(W + "type") == "page") para.Segs.Add(new WSeg("pagebreak", "", rs));
                    else if (f.Depth == 0) para.Segs.Add(new WSeg("break", "\n", rs));
                    break;
                case "sym":
                    var ch = (string?)x.Attribute(W + "char");
                    var glyph = ch?.ToUpperInvariant() switch { "F0A8" or "F06F" or "F071" => "☐", "F0FE" or "F078" or "F0FD" => "☒", "F0FC" => "✓", _ => null };
                    if (glyph is not null && f.Depth == 0) para.Segs.Add(new WSeg("text", glyph, rs));
                    break;
            }
        }
    }

    static void FieldSeg(string instr, string result, XElement? ff, WPara para, WStyle rs)
    {
        var code = instr.Split(' ', 2)[0].ToUpperInvariant();
        switch (code)
        {
            case "PAGE": para.Segs.Add(new WSeg("page", result, rs)); return;
            case "NUMPAGES" or "SECTIONPAGES": para.Segs.Add(new WSeg("pages", result, rs)); return;
            case "FORMTEXT":
            {
                var c = new WControl { Type = "text", Legacy = true, Tag = (string?)ff?.Element(W + "name")?.Attribute(W + "val"), Text = result.Replace("\u2002", "").Trim() };
                var tt = ff?.Element(W + "textInput");
                var kind = (string?)tt?.Element(W + "type")?.Attribute(W + "val");
                if (kind is "date") { c.Type = "date"; c.DateFormat = (string?)tt?.Element(W + "format")?.Attribute(W + "val"); }
                else if (kind is "number") c.Type = "number";
                c.Alias = (string?)ff?.Element(W + "statusText")?.Attribute(W + "val");
                para.Segs.Add(new WSeg("control", c.Text, rs, c)); return;
            }
            case "FORMCHECKBOX":
            {
                var cb = ff?.Element(W + "checkBox");
                var on = (string?)(cb?.Element(W + "checked") ?? cb?.Element(W + "default"))?.Attribute(W + "val") is "1" or "true" || (cb?.Element(W + "checked") is { } ce && ce.Attribute(W + "val") is null);
                var c = new WControl { Type = "checkbox", Legacy = true, Tag = (string?)ff?.Element(W + "name")?.Attribute(W + "val"), Checked = on, Text = on ? "☒" : "☐" };
                para.Segs.Add(new WSeg("control", c.Text, rs, c)); return;
            }
            case "FORMDROPDOWN":
            {
                var dd = ff?.Element(W + "ddList");
                var c = new WControl { Type = "dropdown", Legacy = true, Tag = (string?)ff?.Element(W + "name")?.Attribute(W + "val"), Items = dd?.Elements(W + "listEntry").Select(e => (string?)e.Attribute(W + "val") ?? "").Where(x => x.Length > 0).ToList() ?? [] };
                para.Segs.Add(new WSeg("control", "", rs, c)); return;
            }
        }
        if (instr.StartsWith('=')) { para.Segs.Add(new WSeg("formula", instr, rs)); return; }
        if (result.Length > 0) para.Segs.Add(new WSeg("text", result, rs));
    }

    static string? Border(XElement? b)
    {
        var v = (string?)b?.Attribute(W + "val");
        if (v is null or "nil" or "none") return null;
        var sz = double.TryParse((string?)b!.Attribute(W + "sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s / 8 : 0.5;
        return v switch { "double" => "double", "dashed" or "dashSmallGap" or "dotDash" => "dashed", "dotted" => "dotted", _ => sz >= 2.25 ? "thick" : sz >= 1.25 ? "medium" : "thin" };
    }

    static WTable Table(XElement t, Ctx ctx)
    {
        var tb = new WTable();
        foreach (var g in t.Element(W + "tblGrid")?.Elements(W + "gridCol") ?? []) tb.Grid.Add(Tw((string?)g.Attribute(W + "w")));
        var tblBorders = t.Element(W + "tblPr")?.Element(W + "tblBorders");
        foreach (var tr in RowsOf(t))
        {
            var row = new WRow();
            var trPr = tr.Element(W + "trPr");
            row.Header = trPr?.Element(W + "tblHeader") is not null;
            if (trPr?.Element(W + "trHeight") is { } h) row.Height = Tw((string?)h.Attribute(W + "val"));
            foreach (var tc in CellsOf(tr))
            {
                var cell = new WCell();
                var pr = tc.Element(W + "tcPr");
                cell.GridSpan = int.TryParse((string?)pr?.Element(W + "gridSpan")?.Attribute(W + "val"), out var gs) && gs > 0 ? gs : 1;
                if (pr?.Element(W + "vMerge") is { } vm) cell.VMerge = (string?)vm.Attribute(W + "val") == "restart" ? "restart" : "continue";
                if ((string?)pr?.Element(W + "tcW")?.Attribute(W + "type") is "dxa") cell.Width = Tw((string?)pr!.Element(W + "tcW")!.Attribute(W + "w"));
                if ((string?)pr?.Element(W + "shd")?.Attribute(W + "fill") is { } fill && fill != "auto" && Regex.IsMatch(fill, "^[0-9A-Fa-f]{6}$")) cell.Shading = "#" + fill.ToUpperInvariant();
                var bd = pr?.Element(W + "tcBorders");
                cell.BorderTop = Border(bd?.Element(W + "top")) ?? Border(tblBorders?.Element(W + "insideH")) ?? Border(tblBorders?.Element(W + "top"));
                cell.BorderBottom = Border(bd?.Element(W + "bottom")) ?? Border(tblBorders?.Element(W + "insideH")) ?? Border(tblBorders?.Element(W + "bottom"));
                cell.BorderLeft = Border(bd?.Element(W + "left") ?? bd?.Element(W + "start")) ?? Border(tblBorders?.Element(W + "insideV")) ?? Border(tblBorders?.Element(W + "left") ?? tblBorders?.Element(W + "start"));
                cell.BorderRight = Border(bd?.Element(W + "right") ?? bd?.Element(W + "end")) ?? Border(tblBorders?.Element(W + "insideV")) ?? Border(tblBorders?.Element(W + "right") ?? tblBorders?.Element(W + "end"));
                cell.VAlign = (string?)pr?.Element(W + "vAlign")?.Attribute(W + "val") switch { "center" => "middle", "bottom" => "bottom", _ => "top" };
                foreach (var el in tc.Elements())
                {
                    if (el.Name == W + "p") cell.Paras.Add(Para(el, ctx));
                    else if (el.Name == W + "tbl") cell.Nested.Add(Table(el, ctx));
                    else if (el.Name == W + "sdt")
                    {
                        var tmp = new List<WBlock>(); Block(el, ctx, tmp);
                        foreach (var b in tmp) { if (b.P is not null) cell.Paras.Add(b.P); if (b.T is not null) cell.Nested.Add(b.T); }
                    }
                }
                row.Cells.Add(cell);
            }
            tb.Rows.Add(row);
        }
        if (tb.Grid.Count == 0 && tb.Rows.Count > 0) tb.Grid.AddRange(Enumerable.Repeat(468.0 / Math.Max(1, tb.Rows.Max(r => r.Cells.Sum(c => c.GridSpan))), tb.Rows.Max(r => r.Cells.Sum(c => c.GridSpan))));
        return tb;
    }

    static IEnumerable<XElement> RowsOf(XElement t)
    {
        foreach (var e in t.Elements())
        {
            if (e.Name == W + "tr") yield return e;
            else if (e.Name == W + "sdt" || e.Name == W + "customXml") foreach (var x in (e.Element(W + "sdtContent") ?? e).Elements(W + "tr")) yield return x;
        }
    }

    static IEnumerable<XElement> CellsOf(XElement tr)
    {
        foreach (var e in tr.Elements())
        {
            if (e.Name == W + "tc") yield return e;
            else if (e.Name == W + "sdt" || e.Name == W + "customXml") foreach (var x in (e.Element(W + "sdtContent") ?? e).Elements(W + "tc")) yield return x;
        }
    }
}
