using Microsoft.Win32;

namespace EntraUserSync.Core.Abstractions;

public interface IRegistry
{
    IRegistryKey? OpenSubKey(RegistryHive hive, string subKey, bool writable, RegistryView view);
    IRegistryKey CreateSubKey(RegistryHive hive, string subKey, RegistryView view);
}

public interface IRegistryKey : IDisposable
{
    string[] GetValueNames();
    object? GetValue(string name);
    void SetValue(string name, object value, RegistryValueKind kind);
    void DeleteValue(string name, bool throwOnMissing);
}
