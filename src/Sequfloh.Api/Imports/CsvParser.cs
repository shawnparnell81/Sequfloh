using System.Globalization;
using System.Text;

namespace Sequfloh.Imports;

/// <summary>CSV options: detected on inspection, changeable by the user.</summary>
public sealed record CsvOptions(string Encoding = "utf-8", char Delimiter = ',', bool HasHeader = true, int HeaderRow = 1, string? DateFormat = null,
    char DecimalSeparator = '.', string EmptyValue = "null");

public sealed record CsvColumn(int Index, string Location, string Name, string InferredType, int Blank, IReadOnlyList<string> Samples);
public sealed record CsvAnalysis(string Encoding, char Delimiter, bool HasHeader, int HeaderRow, char DecimalSeparator, string? DateFormat, int TotalRows,
    IReadOnlyList<CsvColumn> Columns, IReadOnlyList<IReadOnlyList<string>> SampleRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Streaming CSV parser: BOM / UTF-8 / UTF-16 / Latin-1 detection, delimiter detection, quoted and escaped values, embedded line breaks,
/// header detection, headerless files, type inference with configurable date format and decimal separator. Rows are read one at a time.
/// </summary>
public static class CsvParser
{
    public static readonly char[] Delimiters = [',', ';', '\t', '|'];
    public static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd-MMM-yyyy", "yyyy/MM/dd"];

    public static Encoding EncodingFor(string name) => name.ToLowerInvariant() switch
    {
        "utf-16le" or "utf-16" => new UnicodeEncoding(false, false),
        "utf-16be" => new UnicodeEncoding(true, false),
        "latin-1" or "iso-8859-1" => System.Text.Encoding.Latin1,
        _ => new UTF8Encoding(false, false),
    };

    /// <summary>Detects the encoding from the byte-order mark, then by checking that the first 64 KB is valid UTF-8; otherwise Latin-1.</summary>
    public static string DetectEncoding(Stream s)
    {
        var buf = new byte[65536];
        var n = s.Read(buf, 0, buf.Length);
        s.Position = 0;
        if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return "utf-8";
        if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return "utf-16le";
        if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return "utf-16be";
        var strict = new UTF8Encoding(false, true);
        // a multi-byte sequence may be cut at the buffer edge; trim up to 3 trailing bytes before validating
        for (var cut = 0; cut <= 3 && cut < n; cut++)
        {
            try { strict.GetString(buf, 0, n - cut); return "utf-8"; } catch (DecoderFallbackException) { if (n < buf.Length) break; }
        }
        return "latin-1";
    }

    /// <summary>Reads records one at a time; handles quotes, doubled quotes and line breaks inside quoted values.</summary>
    public static IEnumerable<string[]> Read(TextReader r, char delimiter)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        bool quoted = false, any = false;
        int c;
        // a byte-order mark survives when the reader is rewound after its first read; it is never part of the data
        if (r.Peek() == '\uFEFF') r.Read();
        while ((c = r.Read()) != -1)
        {
            var ch = (char)c;
            any = true;
            if (quoted)
            {
                if (ch == '"') { if (r.Peek() == '"') { r.Read(); field.Append('"'); } else quoted = false; }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) { quoted = true; continue; }
            if (ch == delimiter) { row.Add(field.ToString()); field.Clear(); continue; }
            if (ch == '\r') { if (r.Peek() == '\n') r.Read(); ch = '\n'; }
            if (ch == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                if (!(row.Count == 1 && row[0].Length == 0)) yield return row.ToArray();
                row.Clear(); any = false;
                continue;
            }
            field.Append(ch);
        }
        if (any) { row.Add(field.ToString()); if (!(row.Count == 1 && row[0].Length == 0)) yield return row.ToArray(); }
    }

    public static char DetectDelimiter(string head)
    {
        var lines = head.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(20).ToList();
        if (lines.Count == 0) return ',';
        static int Count(string line, char d) { var q = false; var n = 0; foreach (var ch in line) { if (ch == '"') q = !q; else if (ch == d && !q) n++; } return n; }
        var best = ','; var bestScore = -1.0;
        foreach (var d in Delimiters)
        {
            var counts = lines.Select(l => Count(l, d)).ToList();
            if (counts[0] == 0) continue;
            var consistent = counts.Count(x => x == counts[0]) / (double)counts.Count;
            var score = consistent * 1000 + counts[0];
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    public static bool TryNumber(string v, char dec, out decimal d)
    {
        var s = v.Trim();
        if (dec == ',') s = s.Replace(".", "").Replace(" ", "").Replace(',', '.');
        else s = s.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d);
    }

    public static bool TryDate(string v, string? format, out DateTime d)
    {
        var s = v.Trim();
        if (format is not null) return DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    public static bool TryBool(string v, out bool b)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "y" or "pass" or "ok" or "x": b = true; return true;
            case "false" or "no" or "n" or "fail": b = false; return true;
            default: b = false; return false;
        }
    }

    public static string Infer(IEnumerable<string> values, char dec, string? dateFormat)
    {
        var vs = values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(1000).ToList();
        if (vs.Count == 0) return "text";
        if (vs.All(v => long.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) return "integer";
        if (vs.All(v => TryNumber(v, dec, out _))) return "number";
        if (vs.All(v => TryDate(v, dateFormat, out _))) return "date";
        if (vs.All(v => TryBool(v, out _))) return "boolean";
        return "text";
    }

    /// <summary>Picks the date format that parses every sample, preferring the first in <see cref="DateFormats"/>.</summary>
    public static string? DetectDateFormat(IEnumerable<string> values)
    {
        var vs = values.Where(v => !string.IsNullOrWhiteSpace(v)).Take(500).ToList();
        if (vs.Count == 0) return null;
        return DateFormats.FirstOrDefault(f => vs.All(v => DateTime.TryParseExact(v.Trim(), f, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)));
    }

    static bool LooksLikeHeader(string[] first, IReadOnlyList<string[]> rest, char dec)
    {
        if (first.Any(string.IsNullOrWhiteSpace)) return false;
        if (first.Distinct(StringComparer.OrdinalIgnoreCase).Count() != first.Length) return false;
        if (first.Any(v => TryNumber(v, dec, out _) || TryDate(v, null, out _))) return false;
        if (rest.Count == 0) return true;
        // a header row has text where the data has numbers or dates in at least one column, or all-text data looks different in shape
        for (var i = 0; i < first.Length; i++)
        {
            var col = rest.Select(r => i < r.Length ? r[i] : "").Where(v => v.Length > 0).ToList();
            if (col.Count > 0 && col.All(v => TryNumber(v, dec, out _) || TryDate(v, null, out _))) return true;
        }
        return first.All(v => v.Length <= 60) && rest.Count > 0;
    }

    /// <summary>Analyzes the file with detected options, or with the options given (user overrides).</summary>
    public static CsvAnalysis Analyze(Stream s, CsvOptions? given, int sampleRows = 20, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var encName = given?.Encoding ?? DetectEncoding(s);
        s.Position = 0;
        using var reader = new StreamReader(s, EncodingFor(encName), detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        var headBuf = new char[16384];
        var hn = reader.ReadBlock(headBuf, 0, headBuf.Length);
        var head = new string(headBuf, 0, hn);
        var delim = given?.Delimiter ?? DetectDelimiter(head);
        var dec = given?.DecimalSeparator ?? (delim == ';' && System.Text.RegularExpressions.Regex.IsMatch(head, @"\d,\d") ? ',' : '.');
        s.Position = 0;
        reader.DiscardBufferedData();

        var headerRow = Math.Max(1, given?.HeaderRow ?? 1);
        var rowsSeen = 0;
        string[]? header = null;
        var first = new List<string[]>();
        var columnsSamples = new List<List<string>>();
        var blanks = new List<int>();
        var total = 0;
        var widths = new Dictionary<int, int>();
        foreach (var row in Read(reader, delim))
        {
            rowsSeen++;
            if ((rowsSeen & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            if (rowsSeen < headerRow) continue;
            if (first.Count < 1001) first.Add(row);
            widths[row.Length] = widths.GetValueOrDefault(row.Length) + 1;
        }
        if (first.Count == 0) return new CsvAnalysis(encName, delim, false, headerRow, dec, null, 0, [], [], ["The file contains no rows."]);
        var hasHeader = given?.HasHeader ?? LooksLikeHeader(first[0], first.Skip(1).Take(50).ToList(), dec);
        var data = hasHeader ? first.Skip(1).ToList() : first;
        total = widths.Values.Sum() - (hasHeader ? 1 : 0);
        var width = Math.Max(first.Max(r => r.Length), 1);
        if (widths.Count > 1) warnings.Add($"Rows have different numbers of values ({string.Join(", ", widths.OrderBy(w => w.Key).Select(w => $"{w.Value} with {w.Key}"))}).");
        header = hasHeader ? first[0] : null;
        var dateFormat = given?.DateFormat;
        var cols = new List<CsvColumn>();
        for (var i = 0; i < width; i++)
        {
            var values = data.Select(r => i < r.Length ? r[i] : "").ToList();
            var df = dateFormat ?? DetectDateFormat(values);
            var type = Infer(values, dec, df);
            if (type == "date" && dateFormat is null) dateFormat = df;
            var name = header is not null && i < header.Length && !string.IsNullOrWhiteSpace(header[i]) ? header[i].Trim() : $"Column {i + 1}";
            cols.Add(new CsvColumn(i, ColumnLetter(i), name, type, values.Count(string.IsNullOrWhiteSpace), values.Where(v => v.Length > 0).Take(5).ToList()));
        }
        return new CsvAnalysis(encName, delim, hasHeader, headerRow, dec, dateFormat, total, cols, data.Take(sampleRows).Select(r => (IReadOnlyList<string>)r).ToList(), warnings);
    }

    public static string ColumnLetter(int i) { var s = ""; i++; while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; } return s; }

    /// <summary>
    /// Escapes a value for a CSV that may be opened in a spreadsheet: values starting with =, +, -, @, tab or carriage return get a leading
    /// apostrophe so they are shown as text, never run as formulas. Values are always quoted.
    /// </summary>
    public static string SafeCell(string? v)
    {
        v ??= "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
