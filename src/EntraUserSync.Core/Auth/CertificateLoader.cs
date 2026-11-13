using System.Security.Cryptography.X509Certificates;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;

namespace EntraUserSync.Core.Auth;

public sealed class CertificateLoader(IClock clock) : ICertificateLoader
{
    public CertificateLoadResult Load(string thumbprint, StoreLocation storeLocation, int expiryWarningDays)
    {
        var sanitized = FieldMappingParser.SanitizeThumbprint(thumbprint);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return new CertificateLoadResult { Error = "Certificate thumbprint is empty after sanitization." };
        }

        try
        {
            using var store = new X509Store(StoreName.My, storeLocation);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, sanitized, validOnly: false);
            if (found.Count == 0)
            {
                return new CertificateLoadResult
                {
                    Error = $"Certificate with thumbprint {sanitized} was not found in {storeLocation}\\My."
                };
            }

            var cert = found[0];
            if (!cert.HasPrivateKey)
            {
                return new CertificateLoadResult
                {
                    Error = $"Certificate {sanitized} was found but has no accessible private key."
                };
            }

            var notAfter = new DateTimeOffset(DateTime.SpecifyKind(cert.NotAfter, DateTimeKind.Local)).ToUniversalTime();
            if (notAfter <= clock.UtcNow)
            {
                return new CertificateLoadResult
                {
                    Error = $"Certificate {sanitized} expired at {notAfter:u}.",
                    NotAfterUtc = notAfter
                };
            }

            var nearExpiry = notAfter <= clock.UtcNow.AddDays(expiryWarningDays);
            return new CertificateLoadResult
            {
                Certificate = cert,
                IsNearExpiry = nearExpiry,
                NotAfterUtc = notAfter
            };
        }
        catch (Exception ex)
        {
            return new CertificateLoadResult { Error = $"Failed to load certificate: {ex.Message}" };
        }
    }
}
