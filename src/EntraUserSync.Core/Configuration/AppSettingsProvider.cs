using System.Text.Json;
using System.Text.Json.Serialization;
using EntraUserSync.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EntraUserSync.Core.Configuration;

public sealed class AppSettingsProvider : IAppSettingsProvider, IDisposable
{
    public const string DefaultConfigPath = @"C:\ProgramData\EntraUserSync\config.json";
    public const string RegistryKeyPath = @"SOFTWARE\EntraUserSync";

    private readonly IRegistry _registry;
    private readonly ILogger<AppSettingsProvider>? _logger;
    private readonly string _configPath;
    private readonly object _gate = new();
    private EffectiveSettings _effective;
    private FileSystemWatcher? _watcher;
    private Timer? _registryPollTimer;
    private string? _lastRegistrySnapshot;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AppSettingsProvider(
        IRegistry registry,
        string? configPath = null,
        ILogger<AppSettingsProvider>? logger = null,
        bool enableWatchers = true)
    {
        _registry = registry;
        _logger = logger;
        _configPath = configPath ?? DefaultConfigPath;
        _effective = Load();

        if (enableWatchers)
        {
            TryStartFileWatcher();
            _registryPollTimer = new Timer(_ => CheckRegistryChanged(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    public event EventHandler? Changed;

    public EffectiveSettings GetEffective()
    {
        lock (_gate)
            return _effective;
    }

    public void Reload()
    {
        var next = Load();
        lock (_gate)
            _effective = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private EffectiveSettings Load()
    {
        var settings = new AppSettings();
        var provenance = new List<SettingProvenance>();
        RecordDefaults(settings, provenance);

        ApplyJsonFile(settings, provenance);
        ApplyRegistry(settings, provenance);

        settings.CertificateThumbprint = FieldMappingParser.SanitizeThumbprint(settings.CertificateThumbprint);
        UpdateProvenanceValue(provenance, nameof(AppSettings.CertificateThumbprint), settings.CertificateThumbprint);

        return new EffectiveSettings { Settings = settings, Provenance = provenance };
    }

    private static void RecordDefaults(AppSettings settings, List<SettingProvenance> provenance)
    {
        void Add(string name, string value) =>
            provenance.Add(new SettingProvenance { Name = name, Value = value, Source = "default" });

        Add(nameof(settings.TenantId), settings.TenantId);
        Add(nameof(settings.ClientId), settings.ClientId);
        Add(nameof(settings.CertificateThumbprint), settings.CertificateThumbprint);
        Add(nameof(settings.CertificateStore), settings.CertificateStore.ToString());
        Add(nameof(settings.Cloud), settings.Cloud.ToString());
        Add(nameof(settings.GraphApiVersion), settings.GraphApiVersion.ToString());
        Add(nameof(settings.FieldMappings), FieldMappingParser.Format(settings.FieldMappings));
        Add(nameof(settings.AllowedUpnSuffixes), string.Join(';', settings.AllowedUpnSuffixes));
        Add(nameof(settings.ClearFieldWhenEmpty), settings.ClearFieldWhenEmpty.ToString());
        Add(nameof(settings.MaxFieldLength), settings.MaxFieldLength.ToString());
        Add(nameof(settings.CertExpiryWarningDays), settings.CertExpiryWarningDays.ToString());
        Add(nameof(settings.SyncIntervalMinutes), settings.SyncIntervalMinutes.ToString());
        Add(nameof(settings.LogPath), settings.LogPath);
        Add(nameof(settings.LogRetentionDays), settings.LogRetentionDays.ToString());
        Add(nameof(settings.RegistryView), settings.RegistryView.ToString());
    }

    private void ApplyJsonFile(AppSettings settings, List<SettingProvenance> provenance)
    {
        if (!File.Exists(_configPath))
            return;

        try
        {
            var json = File.ReadAllText(_configPath);
            var dto = JsonSerializer.Deserialize<ConfigFileDto>(json, JsonOptions);
            if (dto is null)
                return;

            ApplyDto(settings, dto, provenance, $"file:{_configPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read config file {ConfigPath}", _configPath);
            throw new InvalidOperationException($"Failed to read config file '{_configPath}': {ex.Message}", ex);
        }
    }

    private void ApplyRegistry(AppSettings settings, List<SettingProvenance> provenance)
    {
        using var key = _registry.OpenSubKey(RegistryHive.LocalMachine, RegistryKeyPath, writable: false, RegistryView.Registry64)
                     ?? _registry.OpenSubKey(RegistryHive.LocalMachine, RegistryKeyPath, writable: false, RegistryView.Registry32);
        if (key is null)
            return;

        void ApplyString(string name, Action<string> assign)
        {
            var value = key.GetValue(name) as string;
            if (value is null)
                return;
            assign(value);
            UpsertProvenance(provenance, name, value, "registry:HKLM\\" + RegistryKeyPath);
        }

        ApplyString("TenantId", v => settings.TenantId = v);
        ApplyString("ClientId", v => settings.ClientId = v);
        ApplyString("CertificateThumbprint", v => settings.CertificateThumbprint = v);
        ApplyString("CertThumbprint", v => settings.CertificateThumbprint = v);
        ApplyString("CertificateStore", v =>
        {
            if (Enum.TryParse<CertificateStoreLocation>(v, ignoreCase: true, out var store))
                settings.CertificateStore = store;
        });
        ApplyString("CertStore", v =>
        {
            if (Enum.TryParse<CertificateStoreLocation>(v, ignoreCase: true, out var store))
                settings.CertificateStore = store;
        });
        ApplyString("Cloud", v =>
        {
            if (Enum.TryParse<AzureCloud>(v, ignoreCase: true, out var cloud))
                settings.Cloud = cloud;
        });
        ApplyString("GraphApiVersion", v =>
        {
            settings.GraphApiVersion = ParseGraphVersion(v);
        });
        ApplyString("FieldMappings", v =>
        {
            settings.FieldMappings = FieldMappingParser.Parse(v);
        });
        ApplyString("AllowedUpnSuffixes", v =>
        {
            settings.AllowedUpnSuffixes = v.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        });
        ApplyString("ClearFieldWhenEmpty", v =>
        {
            if (bool.TryParse(v, out var b))
                settings.ClearFieldWhenEmpty = b;
            else if (v is "1" or "0")
                settings.ClearFieldWhenEmpty = v == "1";
        });
        ApplyString("ClearWhenEmpty", v =>
        {
            if (bool.TryParse(v, out var b))
                settings.ClearFieldWhenEmpty = b;
            else if (v is "1" or "0")
                settings.ClearFieldWhenEmpty = v == "1";
        });
        ApplyString("MaxFieldLength", v =>
        {
            if (int.TryParse(v, out var n))
                settings.MaxFieldLength = n;
        });
        ApplyString("CertExpiryWarningDays", v =>
        {
            if (int.TryParse(v, out var n))
                settings.CertExpiryWarningDays = n;
        });
        ApplyString("SyncIntervalMinutes", v =>
        {
            if (int.TryParse(v, out var n))
                settings.SyncIntervalMinutes = n;
        });
        ApplyString("LogPath", v => settings.LogPath = v);
        ApplyString("LogRetentionDays", v =>
        {
            if (int.TryParse(v, out var n))
                settings.LogRetentionDays = n;
        });
        ApplyString("RegistryView", v =>
        {
            if (Enum.TryParse<RegistryView>(v, ignoreCase: true, out var view))
                settings.RegistryView = view;
        });

        _lastRegistrySnapshot = SnapshotRegistry(key);
    }

    private static void ApplyDto(AppSettings settings, ConfigFileDto dto, List<SettingProvenance> provenance, string source)
    {
        if (dto.TenantId is not null)
        {
            settings.TenantId = dto.TenantId;
            UpsertProvenance(provenance, nameof(AppSettings.TenantId), dto.TenantId, source);
        }
        if (dto.ClientId is not null)
        {
            settings.ClientId = dto.ClientId;
            UpsertProvenance(provenance, nameof(AppSettings.ClientId), dto.ClientId, source);
        }
        if (dto.CertificateThumbprint is not null)
        {
            settings.CertificateThumbprint = dto.CertificateThumbprint;
            UpsertProvenance(provenance, nameof(AppSettings.CertificateThumbprint), dto.CertificateThumbprint, source);
        }
        if (dto.CertificateStore is not null &&
            Enum.TryParse<CertificateStoreLocation>(dto.CertificateStore, ignoreCase: true, out var store))
        {
            settings.CertificateStore = store;
            UpsertProvenance(provenance, nameof(AppSettings.CertificateStore), store.ToString(), source);
        }
        if (dto.Cloud is not null && Enum.TryParse<AzureCloud>(dto.Cloud, ignoreCase: true, out var cloud))
        {
            settings.Cloud = cloud;
            UpsertProvenance(provenance, nameof(AppSettings.Cloud), cloud.ToString(), source);
        }
        if (dto.GraphApiVersion is not null)
        {
            settings.GraphApiVersion = ParseGraphVersion(dto.GraphApiVersion);
            UpsertProvenance(provenance, nameof(AppSettings.GraphApiVersion), settings.GraphApiVersion.ToString(), source);
        }
        if (dto.FieldMappings is not null)
        {
            if (dto.FieldMappings is JsonElement { ValueKind: JsonValueKind.String } s)
            {
                settings.FieldMappings = FieldMappingParser.Parse(s.GetString());
            }
            else if (dto.FieldMappings is JsonElement { ValueKind: JsonValueKind.Object } obj)
            {
                var list = new List<FieldMapping>();
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Value.TryGetInt32(out var n))
                        list.Add(new FieldMapping { AttributePath = prop.Name, UdfNumber = n });
                }
                settings.FieldMappings = list;
            }
            else if (dto.FieldMappings is Dictionary<string, int> dict)
            {
                settings.FieldMappings = dict.Select(kv => new FieldMapping
                {
                    AttributePath = kv.Key,
                    UdfNumber = kv.Value
                }).ToList();
            }

            UpsertProvenance(provenance, nameof(AppSettings.FieldMappings), FieldMappingParser.Format(settings.FieldMappings), source);
        }
        if (dto.AllowedUpnSuffixes is not null)
        {
            settings.AllowedUpnSuffixes = dto.AllowedUpnSuffixes;
            UpsertProvenance(provenance, nameof(AppSettings.AllowedUpnSuffixes), string.Join(';', dto.AllowedUpnSuffixes), source);
        }
        if (dto.ClearFieldWhenEmpty is not null)
        {
            settings.ClearFieldWhenEmpty = dto.ClearFieldWhenEmpty.Value;
            UpsertProvenance(provenance, nameof(AppSettings.ClearFieldWhenEmpty), settings.ClearFieldWhenEmpty.ToString(), source);
        }
        if (dto.MaxFieldLength is not null)
        {
            settings.MaxFieldLength = dto.MaxFieldLength.Value;
            UpsertProvenance(provenance, nameof(AppSettings.MaxFieldLength), settings.MaxFieldLength.ToString(), source);
        }
        if (dto.CertExpiryWarningDays is not null)
        {
            settings.CertExpiryWarningDays = dto.CertExpiryWarningDays.Value;
            UpsertProvenance(provenance, nameof(AppSettings.CertExpiryWarningDays), settings.CertExpiryWarningDays.ToString(), source);
        }
        if (dto.SyncIntervalMinutes is not null)
        {
            settings.SyncIntervalMinutes = dto.SyncIntervalMinutes.Value;
            UpsertProvenance(provenance, nameof(AppSettings.SyncIntervalMinutes), settings.SyncIntervalMinutes.ToString(), source);
        }
        if (dto.LogPath is not null)
        {
            settings.LogPath = dto.LogPath;
            UpsertProvenance(provenance, nameof(AppSettings.LogPath), settings.LogPath, source);
        }
        if (dto.LogRetentionDays is not null)
        {
            settings.LogRetentionDays = dto.LogRetentionDays.Value;
            UpsertProvenance(provenance, nameof(AppSettings.LogRetentionDays), settings.LogRetentionDays.ToString(), source);
        }
        if (dto.RegistryView is not null && Enum.TryParse<RegistryView>(dto.RegistryView, ignoreCase: true, out var view))
        {
            settings.RegistryView = view;
            UpsertProvenance(provenance, nameof(AppSettings.RegistryView), view.ToString(), source);
        }
    }

    private static GraphApiVersion ParseGraphVersion(string value) => value.Trim().ToLowerInvariant() switch
    {
        "v1.0" or "v1_0" or "1.0" => GraphApiVersion.V1_0,
        "beta" => GraphApiVersion.Beta,
        _ when Enum.TryParse<GraphApiVersion>(value, ignoreCase: true, out var v) => v,
        _ => throw new FormatException($"Invalid GraphApiVersion '{value}'. Use v1.0 or beta.")
    };

    private static void UpsertProvenance(List<SettingProvenance> list, string name, string value, string source)
    {
        list.RemoveAll(p => p.Name == name);
        list.Add(new SettingProvenance { Name = name, Value = value, Source = source });
    }

    private static void UpdateProvenanceValue(List<SettingProvenance> list, string name, string value)
    {
        var existing = list.FirstOrDefault(p => p.Name == name);
        if (existing is null)
            return;
        list.Remove(existing);
        list.Add(new SettingProvenance { Name = name, Value = value, Source = existing.Source });
    }

    private void TryStartFileWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrWhiteSpace(dir))
                return;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += (_, _) => OnConfigFileChanged();
            _watcher.Created += (_, _) => OnConfigFileChanged();
            _watcher.Renamed += (_, _) => OnConfigFileChanged();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not start config file watcher for {ConfigPath}", _configPath);
        }
    }

    private void OnConfigFileChanged()
    {
        try
        {
            Thread.Sleep(250);
            Reload();
            _logger?.LogInformation("Configuration reloaded from file change.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Configuration reload after file change failed.");
        }
    }

    private void CheckRegistryChanged()
    {
        try
        {
            using var key = _registry.OpenSubKey(RegistryHive.LocalMachine, RegistryKeyPath, writable: false, RegistryView.Registry64)
                         ?? _registry.OpenSubKey(RegistryHive.LocalMachine, RegistryKeyPath, writable: false, RegistryView.Registry32);
            var snapshot = key is null ? null : SnapshotRegistry(key);
            if (snapshot == _lastRegistrySnapshot)
                return;
            Reload();
            _logger?.LogInformation("Configuration reloaded from registry change.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Configuration reload after registry poll failed.");
        }
    }

    private static string SnapshotRegistry(IRegistryKey key)
    {
        var names = key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        return string.Join('|', names.Select(n => $"{n}={key.GetValue(n)}"));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _registryPollTimer?.Dispose();
    }

    private sealed class ConfigFileDto
    {
        public string? TenantId { get; set; }
        public string? ClientId { get; set; }
        public string? CertificateThumbprint { get; set; }
        public string? CertificateStore { get; set; }
        public string? Cloud { get; set; }
        public string? GraphApiVersion { get; set; }
        public object? FieldMappings { get; set; }
        public string[]? AllowedUpnSuffixes { get; set; }
        public bool? ClearFieldWhenEmpty { get; set; }
        public int? MaxFieldLength { get; set; }
        public int? CertExpiryWarningDays { get; set; }
        public int? SyncIntervalMinutes { get; set; }
        public string? LogPath { get; set; }
        public int? LogRetentionDays { get; set; }
        public string? RegistryView { get; set; }
    }
}
