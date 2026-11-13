using Microsoft.Win32;

namespace EntraUserSync.Core.Configuration;

public enum AzureCloud
{
    Global,
    USGov,
    USGovDoD,
    China
}

public enum GraphApiVersion
{
    V1_0,
    Beta
}

public enum CertificateStoreLocation
{
    LocalMachine,
    CurrentUser
}

public sealed class FieldMapping
{
    public required string AttributePath { get; init; }
    public required int UdfNumber { get; init; }
}

public sealed class AppSettings
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    // Default CurrentUser: the agent runs as the logged-on user, not LocalSystem.
    public CertificateStoreLocation CertificateStore { get; set; } = CertificateStoreLocation.CurrentUser;
    public AzureCloud Cloud { get; set; } = AzureCloud.Global;
    public GraphApiVersion GraphApiVersion { get; set; } = GraphApiVersion.V1_0;
    public IReadOnlyList<FieldMapping> FieldMappings { get; set; } =
    [
        new FieldMapping { AttributePath = "department", UdfNumber = 2 },
        new FieldMapping { AttributePath = "jobTitle", UdfNumber = 3 }
    ];
    public IReadOnlyList<string> AllowedUpnSuffixes { get; set; } = [];
    public bool ClearFieldWhenEmpty { get; set; }
    public int MaxFieldLength { get; set; } = 255;
    public int CertExpiryWarningDays { get; set; } = 30;
    public int SyncIntervalMinutes { get; set; } = 240;
    public string LogPath { get; set; } = @"C:\ProgramData\EntraUserSync\logs";
    public int LogRetentionDays { get; set; } = 30;
    /// <summary>
    /// TODO: Confirm which registry view the Datto RMM agent reads for CentraStage CustomN values.
    /// Defaulting to Registry64 for 64-bit Windows installs.
    /// </summary>
    public RegistryView RegistryView { get; set; } = RegistryView.Registry64;
    public int StartupDelaySeconds { get; set; } = 15;
    public int DebounceSeconds { get; set; } = 30;

    public AppSettings Clone() => new()
    {
        TenantId = TenantId,
        ClientId = ClientId,
        CertificateThumbprint = CertificateThumbprint,
        CertificateStore = CertificateStore,
        Cloud = Cloud,
        GraphApiVersion = GraphApiVersion,
        FieldMappings = FieldMappings.Select(m => new FieldMapping
        {
            AttributePath = m.AttributePath,
            UdfNumber = m.UdfNumber
        }).ToList(),
        AllowedUpnSuffixes = AllowedUpnSuffixes.ToList(),
        ClearFieldWhenEmpty = ClearFieldWhenEmpty,
        MaxFieldLength = MaxFieldLength,
        CertExpiryWarningDays = CertExpiryWarningDays,
        SyncIntervalMinutes = SyncIntervalMinutes,
        LogPath = LogPath,
        LogRetentionDays = LogRetentionDays,
        RegistryView = RegistryView,
        StartupDelaySeconds = StartupDelaySeconds,
        DebounceSeconds = DebounceSeconds
    };
}

public sealed class SettingProvenance
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Source { get; init; }
}

public sealed class EffectiveSettings
{
    public required AppSettings Settings { get; init; }
    public required IReadOnlyList<SettingProvenance> Provenance { get; init; }
}
