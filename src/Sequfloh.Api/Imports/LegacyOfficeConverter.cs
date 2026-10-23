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

/// <summary>
/// Runs an office suite in headless mode, in a private temporary directory and a fresh profile with macros switched off, with a time limit.
/// One conversion runs at a time so memory stays bounded; a conversion that runs too long is killed with its child processes.
/// </summary>
public sealed class HeadlessOfficeConverter(IConfiguration config) : ILegacyOfficeConverter
{
    private string? Command => config["Imports:LegacyOffice:Command"] is { Length: > 0 } c ? c : Find("soffice") ?? Find("libreoffice");
    private int TimeoutSeconds => config.GetValue("Imports:LegacyOffice:TimeoutSeconds", 90);
    static readonly SemaphoreSlim One = new(1, 1);

    /// <summary>Profile settings: highest macro security, macro execution disabled, no update checks or first-start wizard.</summary>
    const string Profile = """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
        <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job['UpdateCheck']/Arguments"><prop oor:name="AutoCheckEnabled" oor:op="fuse"><value>false</value></prop></item>
        </oor:items>
        """;
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

    /// <summary>A web page opens in the web editor, which needs the export filter named; a spreadsheet saved as a web page is opened by the spreadsheet app.</summary>
    static string Target(string from, string to) => from.TrimStart('.') is "html" ? to == "docx" ? "docx:MS Word 2007 XML" : "xlsx:Calc MS Excel 2007 XML" : to;

    public async Task<Stream> ConvertAsync(Stream input, string fromExtension, string toExtension, CancellationToken ct)
    {
        if (!Available) throw new AppException($"Older .{fromExtension.TrimStart('.')} files cannot be converted on this server right now. Save the file as .{toExtension} and upload it again.");
        await One.WaitAsync(ct);
        try { return await RunAsync(input, fromExtension, toExtension, ct); }
        finally { One.Release(); }
    }

    async Task<Stream> RunAsync(Stream input, string fromExtension, string toExtension, CancellationToken ct)
    {
        var work = Directory.CreateTempSubdirectory("sqf-legacy-");
        try
        {
            var src = Path.Combine(work.FullName, "source." + fromExtension.TrimStart('.'));
            await using (var f = File.Create(src)) await input.CopyToAsync(f, ct);
            var profileDir = Path.Combine(work.FullName, "profile");
            Directory.CreateDirectory(Path.Combine(profileDir, "user"));
            await File.WriteAllTextAsync(Path.Combine(profileDir, "user", "registrymodifications.xcu"), Profile, ct);
            var profile = new Uri(profileDir).AbsoluteUri;
            var psi = new ProcessStartInfo(Command!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = work.FullName };
            if (fromExtension.TrimStart('.') is "html" && toExtension == "xlsx") psi.ArgumentList.Add("--infilter=HTML (StarCalc)");
            foreach (var a in new[] { $"-env:UserInstallation={profile}", "--headless", "--invisible", "--nodefault", "--nologo", "--norestore", "--nolockcheck", "--convert-to", Target(fromExtension, toExtension), "--outdir", work.FullName, src }) psi.ArgumentList.Add(a);
            psi.Environment["HOME"] = work.FullName; // nothing is written outside the temporary directory
            using var p = Process.Start(psi) ?? throw new AppException("The file converter could not start.");
            _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None); _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new AppException("This file took too long to convert. It may be very large or damaged."); }
            var outPath = Path.Combine(work.FullName, "source." + toExtension);
            if (p.ExitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length == 0) throw new AppException("This file could not be converted. It may be damaged or password protected.");
            var ms = new MemoryStream(await File.ReadAllBytesAsync(outPath, ct));
            return ms;
        }
        finally { try { work.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
