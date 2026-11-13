using System.Reflection;

namespace EntraUserSync.Core;

public static class VersionInfo
{
    public static string Current { get; } =
        typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";
}
