namespace Sequfloh.Imports;

/// <summary>Checks an uploaded file before anything reads it: the content must match the type, Office packages must be safe to open, CSV must be text.</summary>
public static class ImportSecurity
{
    static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0];

    /// <summary>Checks the file signature against its type, refuses macros in Office packages (only .xlsm may carry them and they are never run) and binary content in CSV.</summary>
    public static async Task<string?> InspectAsync(Stream s, string ext, CancellationToken ct)
    {
        var head = new byte[8];
        var n = await s.ReadAsync(head, ct);
        s.Position = 0;
        bool Starts(byte[] sig) => n >= sig.Length && head.AsSpan(0, sig.Length).SequenceEqual(sig);
        switch (ext)
        {
            case ".xlsx" or ".xlsm" or ".docx":
                if (!Starts(Zip)) return "The file content is not an Office Open XML document.";
                try
                {
                    using var zip = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
                    long expanded = 0;
                    foreach (var e in zip.Entries)
                    {
                        expanded += e.Length;
                        if (e.FullName.Contains("..")) return "The document contains an unsafe path.";
                        if (ext != ".xlsm" && e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)) return "The document contains macros. Save it without macros (or as .xlsm, whose macros are ignored).";
                        if (e.FullName.Contains("activeX", StringComparison.OrdinalIgnoreCase)) return "The document contains ActiveX controls.";
                    }
                    if (expanded > 1024L * 1024 * 1024) return "The document expands to more than 1 GB.";
                }
                catch (InvalidDataException) { return "The document is damaged."; }
                finally { s.Position = 0; }
                return null;
            case ".xls" or ".doc":
                return Starts(Ole) ? null : "The file content is not a legacy Office document.";
            case ".csv" or ".txt" or ".tsv":
                var buf = new byte[8192];
                var m = await s.ReadAsync(buf, ct);
                s.Position = 0;
                var utf16 = m >= 2 && (buf[0] == 0xFF && buf[1] == 0xFE || buf[0] == 0xFE && buf[1] == 0xFF);
                if (!utf16 && buf.AsSpan(0, m).IndexOf((byte)0) >= 0) return "The file contains binary content, not text.";
                if (m >= 4 && buf.AsSpan(0, 4).SequenceEqual("%PDF"u8)) return "The file content is a PDF, not text.";
                if (Starts(Zip) || Starts(Ole)) return "The file content is an Office document, not text.";
                return null;
            default:
                return "This file type is not accepted.";
        }
    }
}
