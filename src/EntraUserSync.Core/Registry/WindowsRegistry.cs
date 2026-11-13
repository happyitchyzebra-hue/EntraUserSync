using EntraUserSync.Core.Abstractions;
using Microsoft.Win32;

namespace EntraUserSync.Core.Registry;

public sealed class WindowsRegistry : IRegistry
{
    public IRegistryKey? OpenSubKey(RegistryHive hive, string subKey, bool writable, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        var key = baseKey.OpenSubKey(subKey, writable);
        return key is null ? null : new WindowsRegistryKey(key);
    }

    public IRegistryKey CreateSubKey(RegistryHive hive, string subKey, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        var key = baseKey.CreateSubKey(subKey, writable: true)
                  ?? throw new InvalidOperationException($"Failed to create registry key {hive}\\{subKey}.");
        return new WindowsRegistryKey(key);
    }
}

internal sealed class WindowsRegistryKey(RegistryKey key) : IRegistryKey
{
    public string[] GetValueNames() => key.GetValueNames();

    public object? GetValue(string name) => key.GetValue(name);

    public void SetValue(string name, object value, RegistryValueKind kind) =>
        key.SetValue(name, value, kind);

    public void DeleteValue(string name, bool throwOnMissing)
    {
        try
        {
            key.DeleteValue(name, throwOnMissing);
        }
        catch (ArgumentException) when (!throwOnMissing)
        {
            // missing value
        }
    }

    public void Dispose() => key.Dispose();
}
