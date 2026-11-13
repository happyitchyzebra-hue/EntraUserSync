using System.Security.Cryptography.X509Certificates;

namespace EntraUserSync.Core.Abstractions;

public sealed class CertificateLoadResult
{
    public X509Certificate2? Certificate { get; init; }
    public string? Error { get; init; }
    public bool IsNearExpiry { get; init; }
    public DateTimeOffset? NotAfterUtc { get; init; }
    public bool Success => Certificate is not null && Error is null;
}

public interface ICertificateLoader
{
    CertificateLoadResult Load(string thumbprint, StoreLocation storeLocation, int expiryWarningDays);
}
