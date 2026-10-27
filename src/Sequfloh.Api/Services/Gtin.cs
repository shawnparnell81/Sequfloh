namespace Sequfloh.Infrastructure.Services;

/// <summary>UPC / EAN / GTIN box-label barcodes: which scans are barcodes, and one 14-digit key per code so its UPC-A, EAN-13 and GTIN-14 forms match.</summary>
public static class Gtin
{
    /// <summary>All digits, 8 (UPC-E), 12 (UPC-A), 13 (EAN-13) or 14 (GTIN-14) long.</summary>
    public static bool IsBarcode(string? s) => s is { Length: 8 or 12 or 13 or 14 } && s.All(char.IsAsciiDigit);

    /// <summary>The code as GTIN-14 (UPC-E expanded to UPC-A first, then zeros in front), or null when it is not a barcode.</summary>
    public static string? Key(string? s)
    {
        s = s?.Trim();
        if (!IsBarcode(s)) return null;
        if (s!.Length == 8) s = ExpandUpcE(s);
        return s?.PadLeft(14, '0');
    }

    /// <summary>The check digit (last digit) is right for the code.</summary>
    public static bool CheckDigitOk(string s)
    {
        var full = s.Length == 8 ? ExpandUpcE(s) : s;
        if (full is null) return false;
        var body = full[..^1];
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[body.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - sum % 10) % 10 == full[^1] - '0';
    }

    /// <summary>UPC-E (number system 0 or 1) to its 12-digit UPC-A form.</summary>
    public static string? ExpandUpcE(string e)
    {
        if (e.Length != 8 || (e[0] != '0' && e[0] != '1')) return null;
        char ns = e[0], chk = e[7]; var d = e[1..7];
        var body = d[5] switch
        {
            '0' or '1' or '2' => $"{d[0]}{d[1]}{d[5]}0000{d[2]}{d[3]}{d[4]}",
            '3' => $"{d[0]}{d[1]}{d[2]}00000{d[3]}{d[4]}",
            '4' => $"{d[0]}{d[1]}{d[2]}{d[3]}00000{d[4]}",
            _ => $"{d[0]}{d[1]}{d[2]}{d[3]}{d[4]}0000{d[5]}",
        };
        return $"{ns}{body}{chk}";
    }
}
