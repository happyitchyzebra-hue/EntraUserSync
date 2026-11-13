using System.Globalization;
using EntraUserSync.Core.Abstractions;
using Microsoft.Win32;

namespace EntraUserSync.Core.Registry;

public sealed class StatusReporter(IRegistry registry, RegistryView view = RegistryView.Registry64) : IStatusReporter
{
    public const string StatusKeyPath = @"SOFTWARE\EntraUserSync\Status";

    public void Write(SyncStatus status)
    {
        using var key = registry.CreateSubKey(RegistryHive.LocalMachine, StatusKeyPath, view);
        key.SetValue("LastRunUtc", status.LastRunUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue("LastResult", status.LastResult.ToString(), RegistryValueKind.String);
        key.SetValue("LastError", status.LastError ?? string.Empty, RegistryValueKind.String);
        key.SetValue("LastUser", status.LastUser ?? string.Empty, RegistryValueKind.String);
        key.SetValue("LastStage", status.LastStage ?? string.Empty, RegistryValueKind.String);
        key.SetValue(
            "CertExpiresUtc",
            status.CertExpiresUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
            RegistryValueKind.String);
        key.SetValue("Version", status.Version, RegistryValueKind.String);
    }
}
