using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Imports;

/// <summary>
/// Writes text edits back into a Word document. Each edited paragraph keeps its paragraph and run formatting: the new text goes into the
/// paragraph's first text run and the other runs are emptied. Everything else in the package (styles, images, headers, footers) is copied unchanged.
/// </summary>
public static class DocxEditor
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static byte[] Apply(byte[] original, IReadOnlyDictionary<int, string> edits)
    {
        var ms = new MemoryStream();
        ms.Write(original);
        ms.Position = 0;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("word/document.xml") ?? throw new AppException("The document part word/document.xml is missing.");
            XDocument doc;
            using (var s = entry.Open())
            using (var xr = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc = XDocument.Load(xr, LoadOptions.PreserveWhitespace);
            var paras = doc.Descendants(W + "p").ToList();
            foreach (var (at, text) in edits)
            {
                if (at < 0 || at >= paras.Count) throw new AppException("The document changed; reload it and try again.");
                Write(paras[at], text);
            }
            entry.Delete();
            var fresh = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var os = fresh.Open();
            using var xw = XmlWriter.Create(os, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false });
            doc.Save(xw);
        }
        return ms.ToArray();
    }

    /// <summary>The text elements that belong to this paragraph (not to a text box nested in it).</summary>
    static List<XElement> OwnText(XElement p) => p.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == p).ToList();

    static void Write(XElement p, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var texts = OwnText(p);
        XElement run;
        if (texts.Count == 0)
        {
            run = new XElement(W + "r");
            p.Add(run);
        }
        else
        {
            run = texts[0].Parent!;
            foreach (var t in texts.Skip(1)) t.Remove();
            texts[0].Remove();
        }
        // line breaks inside the paragraph that were in the file are replaced by the edited text's own breaks
        foreach (var br in p.Descendants(W + "br").Where(b => (string?)b.Attribute(W + "type") is null or "textWrapping" && b.Ancestors(W + "p").First() == p).ToList()) br.Remove();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Add(new XElement(W + "br"));
            run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), lines[i]));
        }
    }
}
