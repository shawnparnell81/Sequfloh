using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Sequfloh.Infrastructure.Modules.Execution;

/// <summary>
/// Turns an evidence photo into a baseline JPEG the PDF page can draw: EXIF orientation applied, transparency flattened
/// onto white, and the long edge brought down to <see cref="MaxLongEdge"/> pixels.
/// </summary>
public static class ReportPhoto
{
    public const int MaxLongEdge = 1600;
    public const int JpegQuality = 85;
    const long MaxDecodePixels = 40_000_000;

    public static string Unreadable(string caption) => $"Photo could not be shown: {caption}";

    /// <summary>A print block for one evidence file: the picture, or one line when that file cannot be read. Other photos are unaffected.</summary>
    public static PrintBlock Block(byte[]? bytes, string caption, ILogger log)
    {
        if (bytes is null || bytes.Length == 0)
        {
            log.LogWarning("Evidence photo {Caption} could not be included in the PDF: the file is missing", caption);
            return new PrintParagraph(Unreadable(caption));
        }
        var jpeg = BaselineJpeg(bytes, out var error);
        if (jpeg is null)
        {
            log.LogWarning("Evidence photo {Caption} could not be included in the PDF: {Reason}", caption, error);
            return new PrintParagraph(Unreadable(caption));
        }
        return new PrintImage(jpeg, caption);
    }

    public static byte[]? BaselineJpeg(byte[] bytes, out string? error)
    {
        error = null;
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null) { error = "The file is not a picture that can be read."; return null; }
            using var oriented = ReadOriented(codec, out error);
            if (oriented is null) return null;
            var (tw, th) = Fit(oriented.Width, oriented.Height);
            var info = new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var surface = SKSurface.Create(info);
            if (surface is null) { error = "The picture could not be prepared."; return null; }
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(oriented, new SKRect(0, 0, tw, th), new SKSamplingOptions(SKCubicResampler.Mitchell));
            canvas.Flush();
            using var image = surface.Snapshot();
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (encoded is null) { error = "The picture could not be saved for the report."; return null; }
            return encoded.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            error = "The picture could not be read.";
            return null;
        }
    }

    static SKBitmap? ReadOriented(SKCodec codec, out string? error)
    {
        error = null;
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w < 1 || h < 1) { error = "The picture has no size."; return null; }
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if ((long)w * h > MaxDecodePixels)
        {
            var scaled = codec.GetScaledDimensions((float)Math.Sqrt(MaxDecodePixels / ((double)w * h)));
            if (scaled.Width > 0 && scaled.Height > 0)
                info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        }
        var raw = new SKBitmap(info);
        var result = codec.GetPixels(info, raw.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
        {
            raw.Dispose();
            error = "The picture could not be read.";
            return null;
        }
        return Orient(raw, codec.EncodedOrigin);
    }

    static (int W, int H) Fit(int w, int h)
    {
        var edge = Math.Max(w, h);
        if (edge <= MaxLongEdge) return (w, h);
        var scale = (double)MaxLongEdge / edge;
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }

    /// <summary>Turns the stored pixels so the picture is the right way up, and returns that bitmap (the source is disposed when a new one is made).</summary>
    static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return src;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int dw = swap ? src.Height : src.Width, dh = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(dw, dh, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        int w = src.Width, h = src.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(dw, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(dw, dh); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, dh); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop:
                canvas.SetMatrix(new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1)); break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(dw, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom:
                canvas.SetMatrix(new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1)); break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, dh); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Flush();
        src.Dispose();
        return dst;
    }
}
