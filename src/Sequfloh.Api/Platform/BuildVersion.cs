using System.Reflection;

/// <summary>Names the running build: the deployed commit when the host provides it, otherwise the app's version and start time.</summary>
public static class BuildVersion
{
    static readonly string Started = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

    public static string Current(IConfiguration config)
    {
        var commit = config["Build:Version"] ?? config["RENDER_GIT_COMMIT"];
        if (!string.IsNullOrWhiteSpace(commit)) return commit.Trim();
        var v = typeof(BuildVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        return $"{v}-{Started}";
    }
}
