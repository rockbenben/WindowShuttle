using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WindowShuttle.Core;

/// <summary>
/// Selects architecture-specific update archives and computes architecture-isolated cache directories.
/// </summary>
public static class UpdatePackage
{
    private static string NormalizeVersion(string version)
    {
        version = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
            throw new ArgumentException("Expected a stable three-part version (e.g. 1.2.3)", nameof(version));
        return version;
    }

    /// <summary>
    /// Returns "x64" or "arm64" matching the target architecture, or the current process architecture if omitted.
    /// </summary>
    public static string Cpu(Architecture? architecture = null) => (architecture ?? RuntimeInformation.ProcessArchitecture) switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException("Supported update architectures are x64 and arm64")
    };

    /// <summary>
    /// Selects the exact self-contained zip asset for the specified application, version and architecture.
    /// Excludes framework-dependent archives and unrelated assets.
    /// </summary>
    public static string? SelectZip(IEnumerable<string> assets, string appName, string version, Architecture? architecture = null)
    {
        var name = $"{appName}-v{NormalizeVersion(version)}-win-{Cpu(architecture)}.zip";
        return assets.SingleOrDefault(asset => string.Equals(asset, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the architecture-isolated directory where unpacked update files are cached:
    /// &lt;root&gt;\&lt;cpu&gt;\&lt;version&gt;\.
    /// This prevents an existing x64 cache from being reused by an arm64 process (or vice versa).
    /// </summary>
    public static string CacheDirectory(string root, string version, Architecture? architecture = null)
        => Path.Combine(root, Cpu(architecture), NormalizeVersion(version));
}
