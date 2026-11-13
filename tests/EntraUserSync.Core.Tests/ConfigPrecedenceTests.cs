using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;
using Microsoft.Win32;

namespace EntraUserSync.Core.Tests;

public class ConfigPrecedenceTests
{
    [Fact]
    public void Precedence_RegistryOverridesFileAndDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath, """
            {
              "TenantId": "from-file.onmicrosoft.com",
              "ClientId": "11111111-1111-1111-1111-111111111111",
              "CertificateThumbprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "SyncIntervalMinutes": 60,
              "FieldMappings": { "department": 2 }
            }
            """);

        var registry = new FakeRegistry();
        registry.Set(
            RegistryHive.LocalMachine,
            AppSettingsProvider.RegistryKeyPath,
            RegistryView.Registry64,
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["TenantId"] = "from-registry.onmicrosoft.com",
                ["SyncIntervalMinutes"] = "120",
                ["FieldMappings"] = "department=2;jobTitle=3"
            });

        using var provider = new AppSettingsProvider(registry, configPath, enableWatchers: false);
        var effective = provider.GetEffective();

        Assert.Equal("from-registry.onmicrosoft.com", effective.Settings.TenantId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", effective.Settings.ClientId);
        Assert.Equal(120, effective.Settings.SyncIntervalMinutes);
        Assert.Equal(2, effective.Settings.FieldMappings.Count);

        var tenantProvenance = effective.Provenance.Single(p => p.Name == "TenantId");
        Assert.Contains("registry", tenantProvenance.Source, StringComparison.OrdinalIgnoreCase);

        var clientProvenance = effective.Provenance.Single(p => p.Name == "ClientId");
        Assert.Contains("file:", clientProvenance.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Thumbprint_IsSanitized()
    {
        var registry = new FakeRegistry();
        registry.Set(
            RegistryHive.LocalMachine,
            AppSettingsProvider.RegistryKeyPath,
            RegistryView.Registry64,
            new Dictionary<string, object>
            {
                ["TenantId"] = "contoso.onmicrosoft.com",
                ["ClientId"] = "34d26ebe-da2c-4155-81f7-7b7776b78b07",
                ["CertificateThumbprint"] = "9b:f6:93:ef:ab:fd:d1:26:1d:56:68:56:f3:49:bd:b1:ae:c6:89:fe"
            });

        using var provider = new AppSettingsProvider(registry, configPath: Path.Combine(Path.GetTempPath(), "missing-eus.json"), enableWatchers: false);
        Assert.Equal("9BF693EFABFDD1261D566856F349BDB1AEC689FE", provider.GetEffective().Settings.CertificateThumbprint);
    }
}

internal sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string KeyOf(RegistryHive hive, string subKey, RegistryView view) =>
        $"{hive}|{view}|{subKey}";

    public void Set(RegistryHive hive, string subKey, RegistryView view, Dictionary<string, object> values) =>
        _keys[KeyOf(hive, subKey, view)] = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);

    public IRegistryKey? OpenSubKey(RegistryHive hive, string subKey, bool writable, RegistryView view)
    {
        if (!_keys.TryGetValue(KeyOf(hive, subKey, view), out var values))
            return null;
        return new FakeRegistryKey(values, writable);
    }

    public IRegistryKey CreateSubKey(RegistryHive hive, string subKey, RegistryView view)
    {
        var key = KeyOf(hive, subKey, view);
        if (!_keys.TryGetValue(key, out var values))
        {
            values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _keys[key] = values;
        }

        return new FakeRegistryKey(values, writable: true);
    }
}

internal sealed class FakeRegistryKey(Dictionary<string, object> values, bool writable) : IRegistryKey
{
    public string[] GetValueNames() => values.Keys.ToArray();

    public object? GetValue(string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    public void SetValue(string name, object value, RegistryValueKind kind)
    {
        if (!writable)
            throw new InvalidOperationException("Key is not writable.");
        values[name] = value;
    }

    public void DeleteValue(string name, bool throwOnMissing)
    {
        if (!values.Remove(name) && throwOnMissing)
            throw new ArgumentException("missing");
    }

    public void Dispose()
    {
    }
}
