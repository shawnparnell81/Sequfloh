using System.Globalization;
using System.Text;

namespace Sequfloh.Infrastructure.Modules.Reports;

public sealed record ReportParams(DateOnly? From, DateOnly? To, Guid? SiteId, Guid? DepartmentId, Guid? SupplierId = null, Guid? ProductId = null);
public sealed record ReportColumn(string Key, string Label, string Type = "text");
public sealed record ReportChart(string Type, string X, string[] Y);
public sealed record ReportDefinition(string Key, string Title, string Category, string Permission, string Description, bool UsesDates);
public sealed record ReportResult(ReportDefinition Definition, ReportParams Params, List<ReportColumn> Columns, List<Dictionary<string, object?>> Rows, ReportChart? Chart);

/// <summary>Turns a report result into an Excel workbook or a CSV file.</summary>
public static class ReportService
{
    /// <summary>Excel workbook of the report table: header row, typed cells (numbers, dates), text that starts like a formula is stored as text.</summary>
    /// <param name="meta">Optional title block (label, value) written above the table, such as report title, organization, generated on and by, and filters.</param>
    public static byte[] ToXlsx(ReportResult r, string title, IReadOnlyList<(string Label, string Value)>? meta = null)
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var name = new string(title.Select(ch => "[]:*?/\\".Contains(ch) ? '-' : ch).ToArray()); var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name);
        var head = 1;
        if (meta is { Count: > 0 })
        {
            foreach (var (label, value) in meta) { ws.Cell(head, 1).Value = label; ws.Cell(head, 1).Style.Font.Bold = true; ws.Cell(head, 2).SetValue(value); ws.Cell(head, 2).Style.NumberFormat.Format = "@"; head++; }
            head++;
        }
        for (var i = 0; i < r.Columns.Count; i++) { ws.Cell(head, i + 1).Value = r.Columns[i].Label; ws.Cell(head, i + 1).Style.Font.Bold = true; }
        var rowNo = head + 1;
        foreach (var row in r.Rows)
        {
            for (var i = 0; i < r.Columns.Count; i++)
            {
                var c = ws.Cell(rowNo, i + 1);
                switch (row.GetValueOrDefault(r.Columns[i].Key))
                {
                    case null: break;
                    case int n: c.Value = n; break;
                    case long n: c.Value = n; break;
                    case decimal n: c.Value = n; break;
                    case double n: c.Value = n; break;
                    case bool b: c.Value = b; break;
                    case DateOnly d: c.Value = d.ToDateTime(TimeOnly.MinValue); c.Style.DateFormat.Format = "yyyy-mm-dd"; break;
                    case DateTimeOffset t: c.Value = t.UtcDateTime; c.Style.DateFormat.Format = "yyyy-mm-dd hh:mm"; break;
                    case var v: c.SetValue(v.ToString() ?? ""); c.Style.NumberFormat.Format = "@"; break;
                }
            }
            rowNo++;
        }
        ws.SheetView.FreezeRows(head);
        ws.Columns().AdjustToContents(head, Math.Min(rowNo, head + 200));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static string ToCsv(ReportResult r)
    {
        var sb = new StringBuilder();
        static string Esc(object? v)
        {
            var s = v switch { null => "", DateOnly d => d.ToString("yyyy-MM-dd"), DateTimeOffset t => t.ToString("u"), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
            if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; // spreadsheet formula injection guard
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        sb.AppendLine(string.Join(',', r.Columns.Select(c => Esc(c.Label))));
        foreach (var row in r.Rows) sb.AppendLine(string.Join(',', r.Columns.Select(c => Esc(row.GetValueOrDefault(c.Key)))));
        return sb.ToString();
    }
}
