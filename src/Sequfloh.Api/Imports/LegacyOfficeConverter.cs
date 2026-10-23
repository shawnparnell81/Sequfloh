using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Imports;

/// <summary>
/// Adapter for the legacy binary Office formats (XLS, DOC): converts them to XLSX or DOCX so the same readers handle them. The original is
/// kept untouched; only the converted copy is read. When no converter is configured the adapter reports that the format needs one.
/// </summary>
public interface ILegacyOfficeConverter
{
    bool Available { get; }
    /// <summary>Converts <paramref name="input"/> (an .xls or .doc file) to <paramref name="toExtension"/> (xlsx or docx).</summary>
    Task<Stream> ConvertAsync(Stream input, string fromExtension, string toExtension, CancellationToken ct);
}

/// <summary>Runs an office suite in headless mode, in a private temporary directory and profile, with a time limit. Macros are disabled by the suite in headless conversion.</summary>
public sealed class HeadlessOfficeConverter(IConfiguration config) : ILegacyOfficeConverter
{
    private string? Command => config["Imports:LegacyOffice:Command"] is { Length: > 0 } c ? c : Find("soffice") ?? Find("libreoffice");
    private int TimeoutSeconds => config.GetValue("Imports:LegacyOffice:TimeoutSeconds", 120);
    public bool Available => !config.GetValue("Imports:LegacyOffice:Disabled", false) && Command is not null;

    static string? Find(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>A web page opens in the web editor, which needs the Word export filter named.</summary>
    static string Target(string from, string to) => from.TrimStart('.') is "html" && to == "docx" ? "docx:MS Word 2007 XML" : to;

    public async Task<Stream> ConvertAsync(Stream input, string fromExtension, string toExtension, CancellationToken ct)
    {
        if (!Available) throw new AppException($"Legacy .{fromExtension.TrimStart('.')} files need the office conversion adapter, which is not configured on this server. Save the file as .{toExtension} and upload it again.");
        var work = Directory.CreateTempSubdirectory("sqf-legacy-");
        try
        {
            var src = Path.Combine(work.FullName, "source." + fromExtension.TrimStart('.'));
            await using (var f = File.Create(src)) await input.CopyToAsync(f, ct);
            var profile = new Uri(Path.Combine(work.FullName, "profile")).AbsoluteUri;
            var psi = new ProcessStartInfo(Command!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work.FullName };
            foreach (var a in new[] { $"-env:UserInstallation={profile}", "--headless", "--norestore", "--nolockcheck", "--convert-to", Target(fromExtension, toExtension), "--outdir", work.FullName, src }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new AppException("The office conversion adapter could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new AppException("The legacy file took too long to convert."); }
            var outPath = Path.Combine(work.FullName, "source." + toExtension);
            if (p.ExitCode != 0 || !File.Exists(outPath)) throw new AppException("The legacy file could not be converted; it may be damaged or password protected.");
            var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath, ct));
            return ms;
        }
        finally { try { work.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
