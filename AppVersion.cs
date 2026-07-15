using System.Reflection;

namespace YamuraView;

/// <summary>
/// Runtime view of the version the csproj's VersionPrefix/VersionSuffix stamped into the
/// assembly (and the exe's file properties) - read back via reflection rather than stored
/// separately, so the About box and the exe properties can't drift apart.
/// </summary>
public static class AppVersion
{
    /// <summary>Major.minor.build, e.g. "1.0.0".</summary>
    public static string Number { get; }

    /// <summary>Prerelease status from the version suffix, capitalized (e.g. "Beta");
    /// "Release" when the csproj sets no VersionSuffix.</summary>
    public static string Status { get; }

    static AppVersion()
    {
        string info = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // strip semver build metadata ("+..."), e.g. a source-control hash
        int metadataStart = info.IndexOf('+');
        if (metadataStart >= 0)
        {
            info = info[..metadataStart];
        }

        int suffixStart = info.IndexOf('-');
        if (suffixStart >= 0)
        {
            Number = info[..suffixStart];
            string suffix = info[(suffixStart + 1)..];
            Status = char.ToUpperInvariant(suffix[0]) + suffix[1..];
        }
        else
        {
            Number = info;
            Status = "Release";
        }
    }
}
