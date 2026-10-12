using System.Text.RegularExpressions;

namespace Modelry.Core.Gateway;

/// <summary>
/// What the connected Content Manager can do, derived from the Core Service API version reported at login
/// (e.g. "9.1.0.1234", "9.5.0.21614", "10.1.0.4567"). One proxy (CoreService201701) is used for every supported version;
/// features that differ between versions are switched on these flags, and calls that turn out to be unsupported at
/// runtime are reported per audit (see AuditSnapshot.Unavailable) rather than failing the run.
/// </summary>
public sealed partial class CmsCapabilities
{
    /// <summary>Oldest Tridion Sites version Modelry supports.</summary>
    public static readonly Version Minimum = new(9, 1);
    /// <summary>The Build wizard (creating schemas, templates, pages) is tested on this version and later.</summary>
    public static readonly Version BuildTested = new(10, 0);

    public string Raw { get; init; } = "";
    public bool IsDemo { get; init; }
    /// <summary>Adobe Experience Manager (version string starts with "AEM").</summary>
    public bool IsAem { get; init; }
    /// <summary>AEM as a Cloud Service.</summary>
    public bool IsCloud { get; init; }
    /// <summary>Parsed version; null when the string could not be read (treated as current).</summary>
    public Version? Version { get; init; }

    public static CmsCapabilities From(string? apiVersion)
    {
        var raw = apiVersion?.Trim() ?? "";
        if (raw.StartsWith("Demo", StringComparison.OrdinalIgnoreCase))
            return new CmsCapabilities { Raw = raw, IsDemo = true, IsAem = raw.Contains("AEM", StringComparison.OrdinalIgnoreCase) };
        if (raw.StartsWith("AEM", StringComparison.OrdinalIgnoreCase))
        {
            var cloud = raw.Contains("Cloud", StringComparison.OrdinalIgnoreCase);
            var am = VersionPattern().Match(raw);
            Version? av = null;
            if (!cloud && am.Success && int.TryParse(am.Groups[1].Value, out var amj))
                av = new Version(amj, int.TryParse(am.Groups[2].Value, out var amn) ? amn : 0, int.TryParse(am.Groups[3].Value, out var ab) ? ab : 0);
            return new CmsCapabilities { Raw = raw, IsAem = true, IsCloud = cloud, Version = av };
        }
        var m = VersionPattern().Match(raw);
        Version? v = null;
        if (m.Success && int.TryParse(m.Groups[1].Value, out var major))
        {
            var minor = int.TryParse(m.Groups[2].Value, out var mi) ? mi : 0;
            var build = int.TryParse(m.Groups[3].Value, out var b) ? b : 0;
            v = new Version(major, minor, build);
        }
        return new CmsCapabilities { Raw = raw, Version = v };
    }

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
    private static partial Regex VersionPattern();

    private bool AtLeast(int major, int minor) => Version is null || IsDemo || Version >= new Version(major, minor);

    /// <summary>True when the version is known and older than <see cref="Minimum"/>.</summary>
    public bool BelowMinimum => !IsDemo && !IsAem && Version is not null && Version < Minimum;
    /// <summary>Build wizard not tested on this version (9.x): show a warning, do not block.</summary>
    public bool BuildUntested => !IsDemo && !IsAem && Version is not null && Version < BuildTested;
    /// <summary>Native regions and Region schemas (Tridion Sites 9.0+).</summary>
    public bool HasRegions => AtLeast(9, 0);
    /// <summary>Access Management (OAuth client credentials) for the Core Service (Tridion Sites 9.5+).</summary>
    public bool HasAccessManagement => AtLeast(9, 5);
    /// <summary>Publishing by target type / purpose (Staging, Live) rather than publication targets (Tridion Sites 9.0+).</summary>
    public bool HasTargetTypePublishing => AtLeast(9, 0);

    /// <summary>"Tridion Sites 10.1" / "Tridion Sites 9.5" / "Demo mode".</summary>
    public string Display => IsDemo ? "Demo mode"
        : IsAem ? (IsCloud ? "AEM as a Cloud Service" : Version is null ? "AEM (version unknown)" : $"AEM {Version.Major}.{Version.Minor}")
        : Version is null ? (Raw.Length > 0 ? Raw : "Unknown version")
        : $"Tridion Sites {Version.Major}.{Version.Minor}";
}
