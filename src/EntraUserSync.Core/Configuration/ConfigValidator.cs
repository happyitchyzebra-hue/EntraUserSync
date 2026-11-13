namespace EntraUserSync.Core.Configuration;

public interface IConfigValidator
{
    ConfigValidationResult Validate(AppSettings settings);
}

public sealed class ConfigValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}

public sealed class ConfigValidator : IConfigValidator
{
    public ConfigValidationResult Validate(AppSettings settings)
    {
        var result = new ConfigValidationResult();

        if (string.IsNullOrWhiteSpace(settings.TenantId))
            result.Errors.Add("TenantId is required.");
        else if (!LooksLikeTenantId(settings.TenantId))
            result.Errors.Add("TenantId must be a GUID or a verified domain name (e.g. contoso.onmicrosoft.com).");

        if (string.IsNullOrWhiteSpace(settings.ClientId))
            result.Errors.Add("ClientId is required.");
        else if (!Guid.TryParse(settings.ClientId, out _))
            result.Errors.Add("ClientId must be a GUID (application/client ID).");

        var thumb = FieldMappingParser.SanitizeThumbprint(settings.CertificateThumbprint);
        if (string.IsNullOrWhiteSpace(thumb))
            result.Errors.Add("CertificateThumbprint is required.");
        else if (thumb.Length is not (40 or 64))
            result.Errors.Add($"CertificateThumbprint must be 40 (SHA-1) or 64 (SHA-256) hex characters after sanitizing; got {thumb.Length}.");

        if (settings.FieldMappings is null || settings.FieldMappings.Count == 0)
            result.Errors.Add("FieldMappings must contain at least one mapping.");
        else
        {
            var seenUdf = new HashSet<int>();
            foreach (var mapping in settings.FieldMappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.AttributePath))
                    result.Errors.Add("FieldMappings contains an empty attribute path.");
                if (mapping.UdfNumber is < 1 or > 30)
                    result.Errors.Add($"FieldMappings UDF number {mapping.UdfNumber} is outside 1-30.");
                if (!seenUdf.Add(mapping.UdfNumber))
                    result.Errors.Add($"FieldMappings maps UDF {mapping.UdfNumber} more than once.");
            }
        }

        if (settings.MaxFieldLength is < 1 or > 2048)
            result.Errors.Add("MaxFieldLength must be between 1 and 2048.");

        if (settings.CertExpiryWarningDays is < 1 or > 365)
            result.Errors.Add("CertExpiryWarningDays must be between 1 and 365.");

        if (settings.SyncIntervalMinutes is < 1 or > 10080)
            result.Errors.Add("SyncIntervalMinutes must be between 1 and 10080 (7 days).");

        if (settings.LogRetentionDays is < 1 or > 3650)
            result.Errors.Add("LogRetentionDays must be between 1 and 3650.");

        if (string.IsNullOrWhiteSpace(settings.LogPath))
            result.Errors.Add("LogPath is required.");

        foreach (var suffix in settings.AllowedUpnSuffixes)
        {
            if (string.IsNullOrWhiteSpace(suffix) || suffix.Contains('@', StringComparison.Ordinal))
                result.Errors.Add($"AllowedUpnSuffixes entry '{suffix}' is invalid. Use domain only (e.g. contoso.com).");
        }

        return result;
    }

    private static bool LooksLikeTenantId(string value) =>
        Guid.TryParse(value, out _) || (value.Contains('.', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal));
}
