using SkiaSharp;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>Chart pictures for the monthly report. An uploaded picture replaces the generated one.</summary>
public static class MonthlyReportCharts
{
    public static byte[] Trend(IReadOnlyList<TrendMonth> months)
    {
        var claims = months.Select(m => (double)(m.TotalClaims ?? 0)).ToArray();
        var alerts = months.Select(m => (double)(m.ProductAlerts ?? 0)).ToArray();
        var labels = months.Select(m => m.Label).ToArray();
        if (claims.All(v => v == 0) && alerts.All(v => v == 0))
            return Note("Six-month trend", "No claim or product alert counts for these months.");
        return Bars("Six-month trend", labels, [("Total Claims", claims, new SKColor(31, 78, 121)), ("Product Alerts", alerts, new SKColor(46, 125, 50))]);
    }

    public static byte[] HighReturn(IReadOnlyList<(string Label, double Value)> rows)
    {
        var data = rows.Where(r => r.Value > 0).Take(10).ToList();
        if (data.Count == 0) return Note("Top returning parts", "No part counts entered.");
        return Bars("Top returning parts", data.Select(r => r.Label).ToArray(), [("Claims", data.Select(r => r.Value).ToArray(), new SKColor(31, 78, 121))]);
    }

    public static byte[] Vehicles(IReadOnlyList<(string Label, double Value)> rows)
    {
        var data = rows.Where(r => r.Value > 0).Take(10).ToList();
        if (data.Count == 0) return Note("Top vehicle models", "No vehicle counts entered.");
        return Bars("Top vehicle models", data.Select(r => r.Label).ToArray(), [("Claims", data.Select(r => r.Value).ToArray(), new SKColor(31, 78, 121))]);
    }

    public static byte[] Note(string title, string message)
    {
        using var surface = SKSurface.Create(new SKImageInfo(980, 280));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var border = new SKPaint { Color = new SKColor(176, 183, 195), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        canvas.DrawRect(8, 8, 964, 264, border);
        using var titlePaint = new SKPaint { Color = new SKColor(31, 78, 121), IsAntialias = true };
        using var titleFont = new SKFont(SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold), 22);
        using var bodyFont = new SKFont(SKTypeface.FromFamilyName("sans-serif"), 18);
        canvas.DrawText(title, 28, 56, titleFont, titlePaint);
        using var body = new SKPaint { Color = new SKColor(60, 64, 67), IsAntialias = true };
        canvas.DrawText(message, 28, 110, bodyFont, body);
        return Jpeg(surface);
    }

    static byte[] Bars(string title, string[] labels, (string Name, double[] Values, SKColor Color)[] series)
    {
        const int w = 980, h = 420, left = 64, right = 24, top = 56, bottom = 78;
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var titleFont = new SKFont(SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold), 20);
        using var labelFont = new SKFont(SKTypeface.FromFamilyName("sans-serif"), 13);
        using var titlePaint = new SKPaint { Color = new SKColor(31, 78, 121), IsAntialias = true };
        using var axis = new SKPaint { Color = new SKColor(120, 128, 138), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawText(title, left, 32, titleFont, titlePaint);
        var max = series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;
        var plotW = w - left - right;
        var plotH = h - top - bottom;
        var groups = Math.Max(1, labels.Length);
        var groupW = plotW / groups;
        var barW = Math.Max(6, (groupW * 0.7) / Math.Max(1, series.Length));
        canvas.DrawLine(left, top + plotH, left + plotW, top + plotH, axis);
        for (var i = 0; i < labels.Length; i++)
        {
            for (var s = 0; s < series.Length; s++)
            {
                var value = i < series[s].Values.Length ? series[s].Values[i] : 0;
                var bh = plotH * (value / max);
                var x = left + i * groupW + (groupW - series.Length * barW) / 2 + s * barW;
                var y = top + plotH - bh;
                using var fill = new SKPaint { Color = series[s].Color, IsAntialias = true };
                canvas.DrawRect((float)x, (float)y, (float)barW - 2, (float)bh, fill);
            }
            var label = labels[i].Length > 14 ? labels[i][..12] + "…" : labels[i];
            using var lp = new SKPaint { Color = new SKColor(60, 64, 67), IsAntialias = true };
            canvas.DrawText(label, left + i * groupW + 4, top + plotH + 22, labelFont, lp);
        }
        var lx = left;
        for (var s = 0; s < series.Length; s++)
        {
            using var fill = new SKPaint { Color = series[s].Color, IsAntialias = true };
            canvas.DrawRect(lx, h - 28, 14, 14, fill);
            using var lp = new SKPaint { Color = new SKColor(60, 64, 67), IsAntialias = true };
            canvas.DrawText(series[s].Name, lx + 18, h - 16, labelFont, lp);
            lx += 18 + (int)labelFont.MeasureText(series[s].Name) + 24;
        }
        return Jpeg(surface);
    }

    static byte[] Jpeg(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
