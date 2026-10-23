using System.Text;
using System.Text.RegularExpressions;

namespace Sequfloh.Imports;

/// <summary>
/// Single-file web pages (MHT, "Web Archive") saved by Word or Excel: the HTML part is taken out so it can be converted like a web page.
/// Embedded pictures are left out. Nothing in the file is run.
/// </summary>
public static class MhtReader
{
    static MhtReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static byte[] ToHtml(byte[] mht)
    {
        var text = Encoding.Latin1.GetString(mht);
        var boundary = Regex.Match(text, @"boundary\s*=\s*""?([^""\r\n;]+)""?", RegexOptions.IgnoreCase);
        var parts = boundary.Success ? text.Split("--" + boundary.Groups[1].Value.Trim()) : [text];
        foreach (var part in parts)
        {
            var split = Regex.Match(part, @"\r?\n\r?\n");
            if (!split.Success) continue;
            var head = part[..split.Index];
            if (!Regex.IsMatch(head, @"Content-Type:\s*text/html", RegexOptions.IgnoreCase)) continue;
            var body = part[(split.Index + split.Length)..];
            var charset = Regex.Match(head, @"charset\s*=\s*""?([\w-]+)", RegexOptions.IgnoreCase) is { Success: true } c ? c.Groups[1].Value : "utf-8";
            var enc = Regex.Match(head, @"Content-Transfer-Encoding:\s*([\w-]+)", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            byte[] raw = enc switch
            {
                "base64" => Convert.FromBase64String(Regex.Replace(body, @"\s", "")),
                "quoted-printable" => QuotedPrintable(body),
                _ => Encoding.Latin1.GetBytes(body),
            };
            Encoding source;
            try { source = Encoding.GetEncoding(charset); } catch (ArgumentException) { source = Encoding.UTF8; }
            return Encoding.UTF8.GetBytes(Regex.Replace(source.GetString(raw), @"charset\s*=\s*""?[\w-]+""?", "charset=utf-8", RegexOptions.IgnoreCase));
        }
        throw new Application.Abstractions.AppException("This web archive (MHT) has no page content to read.");
    }

    static byte[] QuotedPrintable(string s)
    {
        s = Regex.Replace(s, @"=\r?\n", "");
        var o = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { o.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else o.Add((byte)s[i]);
        }
        return [.. o];
    }
}
