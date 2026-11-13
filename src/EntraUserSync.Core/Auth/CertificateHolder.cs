using System.Security.Cryptography.X509Certificates;

namespace EntraUserSync.Core.Auth;

/// <summary>
/// Mutable holder so the orchestrator can load a cert and MSAL can consume it without a circular dependency.
/// </summary>
public sealed class CertificateHolder
{
    private X509Certificate2? _certificate;

    public X509Certificate2 Current =>
        _certificate ?? throw new InvalidOperationException("Certificate has not been loaded yet.");

    public void Set(X509Certificate2 certificate) => _certificate = certificate;

    public void Clear() => _certificate = null;
}
