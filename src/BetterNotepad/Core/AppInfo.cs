using System.Globalization;
using System.Reflection;

namespace BetterNotepad.Core;

/// <summary>
/// Version information baked in at build time (Directory.Build.props):
/// InformationalVersion = "MAJOR.MINOR.PATCH+commit.yyyyMMdd".
/// </summary>
public static class AppInfo
{
    public const string ProductName = "BetterNotepad";
    public const string RepositoryUrl = "https://github.com/NemeWT10/BetterNotepad";
    public const string ReleasesApi = "https://api.github.com/repos/NemeWT10/BetterNotepad/releases/latest";

    public static string Version { get; }
    public static string Commit { get; }
    public static DateTime? BuildDate { get; }
    public static Version SemVer { get; }

    static AppInfo()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        Version = plus >= 0 ? info[..plus] : info;
        Commit = "local";
        if (plus >= 0)
        {
            var meta = info[(plus + 1)..].Split('.');
            if (meta.Length > 0 && meta[0].Length > 0) Commit = meta[0];
            if (meta.Length > 1 && DateTime.TryParseExact(meta[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                BuildDate = d;
        }
        SemVer = TryParseSemVer(Version) ?? new Version(0, 0, 0);
    }

    public static string BuildDateText => BuildDate?.ToString("yyyy-MM-dd") ?? "—";

    /// <summary>Parses "1.2.3" or "v1.2.3" (pre-release suffix ignored).</summary>
    public static Version? TryParseSemVer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(['-', '+']);
        if (cut >= 0) t = t[..cut];
        return System.Version.TryParse(t, out var v) ? new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0)) : null;
    }
}
