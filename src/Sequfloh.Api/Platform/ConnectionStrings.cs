using Npgsql;

namespace Sequfloh.Platform;

public static class ConnectionStrings
{
    /// <summary>
    /// Accepts either a key=value connection string or a postgres:// URL (the form Neon and Render show) and returns a Npgsql connection string.
    /// </summary>
    public static string Normalize(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return value;
        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]).ToLowerInvariant();
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (key)
            {
                case "sslmode": b.SslMode = Enum.Parse<SslMode>(val.Replace("-", ""), true); break;
                case "channel_binding": b.ChannelBinding = Enum.Parse<ChannelBinding>(val, true); break;
                // other URL options (for example options=endpoint%3D...) are not needed by Npgsql
            }
        }
        // a free database may be asleep; give it time to wake up on the first connection
        b.Timeout = 30;
        b.CommandTimeout = 60;
        b.KeepAlive = 30;
        return b.ConnectionString;
    }
}
