using System.Security.Cryptography.X509Certificates;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace EntraUserSync.Core.Auth;

public sealed class MsalTokenProvider : ITokenProvider
{
    private readonly Func<AppSettings> _settingsFactory;
    private readonly Func<X509Certificate2> _certificateFactory;
    private readonly ILogger<MsalTokenProvider>? _logger;
    private readonly object _gate = new();
    private IConfidentialClientApplication? _app;
    private string? _cachedAuthority;
    private string? _cachedClientId;

    public MsalTokenProvider(
        Func<AppSettings> settingsFactory,
        Func<X509Certificate2> certificateFactory,
        ILogger<MsalTokenProvider>? logger = null)
    {
        _settingsFactory = settingsFactory;
        _certificateFactory = certificateFactory;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsFactory();
        var (authorityHost, _, scope) = CloudEndpoints.Resolve(settings.Cloud);
        var authority = $"{authorityHost}/{settings.TenantId}";
        var app = GetOrCreateApp(settings, authority);

        var result = await app.AcquireTokenForClient([scope])
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger?.LogDebug("Acquired Graph token. ExpiresOn={ExpiresOn:u}", result.ExpiresOn);
        return result.AccessToken;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _app = null;
            _cachedAuthority = null;
            _cachedClientId = null;
        }
    }

    private IConfidentialClientApplication GetOrCreateApp(AppSettings settings, string authority)
    {
        lock (_gate)
        {
            if (_app is not null &&
                string.Equals(_cachedAuthority, authority, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_cachedClientId, settings.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                return _app;
            }

            var cert = _certificateFactory();
            _app = ConfidentialClientApplicationBuilder
                .Create(settings.ClientId)
                .WithAuthority(authority)
                .WithCertificate(cert)
                .Build();
            _cachedAuthority = authority;
            _cachedClientId = settings.ClientId;
            return _app;
        }
    }
}
