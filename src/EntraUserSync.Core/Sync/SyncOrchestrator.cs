using System.Security.Cryptography.X509Certificates;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Auth;
using EntraUserSync.Core.Configuration;
using EntraUserSync.Core.Graph;
using EntraUserSync.Core.Udf;
using Microsoft.Extensions.Logging;

namespace EntraUserSync.Core.Sync;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly IAppSettingsProvider _settingsProvider;
    private readonly IConfigValidator _validator;
    private readonly IUserDetector _userDetector;
    private readonly ICertificateLoader _certificateLoader;
    private readonly CertificateHolder _certificateHolder;
    private readonly ITokenProvider _tokenProvider;
    private readonly IGraphUserClient _graphUserClient;
    private readonly IUdfWriter _udfWriter;
    private readonly IStatusReporter _statusReporter;
    private readonly IClock _clock;
    private readonly ILogger<SyncOrchestrator>? _logger;
    private readonly string _version;

    public SyncOrchestrator(
        IAppSettingsProvider settingsProvider,
        IConfigValidator validator,
        IUserDetector userDetector,
        ICertificateLoader certificateLoader,
        CertificateHolder certificateHolder,
        ITokenProvider tokenProvider,
        IGraphUserClient graphUserClient,
        IUdfWriter udfWriter,
        IStatusReporter statusReporter,
        IClock clock,
        ILogger<SyncOrchestrator>? logger = null,
        string? version = null)
    {
        _settingsProvider = settingsProvider;
        _validator = validator;
        _userDetector = userDetector;
        _certificateLoader = certificateLoader;
        _certificateHolder = certificateHolder;
        _tokenProvider = tokenProvider;
        _graphUserClient = graphUserClient;
        _udfWriter = udfWriter;
        _statusReporter = statusReporter;
        _clock = clock;
        _logger = logger;
        _version = version ?? VersionInfo.Current;
    }


    public async Task<SyncRunResult> RunAsync(SyncRunOptions options, CancellationToken cancellationToken = default)
    {
        var stage = "config";
        string? user = null;
        DateTimeOffset? certExpires = null;

        try
        {
            var effective = _settingsProvider.GetEffective();
            var settings = effective.Settings;
            var validation = _validator.Validate(settings);
            if (!validation.IsValid)
            {
                var message = string.Join(" ", validation.Errors);
                return Finish(SyncExitCode.Config, SyncResultKind.Failed, message, user, stage, certExpires, writeStatus: !options.WhatIf);
            }

            stage = "user";
            string upn;
            if (!string.IsNullOrWhiteSpace(options.OverrideUpn))
            {
                upn = options.OverrideUpn.Trim();
                user = upn;
            }
            else
            {
                var detection = _userDetector.Detect(settings.AllowedUpnSuffixes);
                switch (detection.Outcome)
                {
                    case UserDetectionOutcome.Found when detection.User is not null:
                        upn = detection.User.Upn;
                        user = upn;
                        break;
                    case UserDetectionOutcome.NoUser:
                    case UserDetectionOutcome.LocalAccount:
                    case UserDetectionOutcome.SuffixNotAllowed:
                    case UserDetectionOutcome.AmbiguousSessions:
                        return Finish(
                            SyncExitCode.Ok,
                            SyncResultKind.Skipped,
                            detection.Message ?? detection.Outcome.ToString(),
                            detection.User?.Upn,
                            stage,
                            certExpires,
                            writeStatus: !options.WhatIf);
                    default:
                        return Finish(
                            SyncExitCode.UserDetection,
                            SyncResultKind.Failed,
                            detection.Message ?? "User detection failed.",
                            null,
                            stage,
                            certExpires,
                            writeStatus: !options.WhatIf);
                }
            }

            stage = "certificate";
            var storeLocation = settings.CertificateStore == CertificateStoreLocation.CurrentUser
                ? StoreLocation.CurrentUser
                : StoreLocation.LocalMachine;
            var certResult = _certificateLoader.Load(
                settings.CertificateThumbprint,
                storeLocation,
                settings.CertExpiryWarningDays);
            certExpires = certResult.NotAfterUtc;

            if (!certResult.Success || certResult.Certificate is null)
            {
                return Finish(
                    SyncExitCode.CertAuth,
                    SyncResultKind.Failed,
                    certResult.Error ?? "Certificate load failed.",
                    user,
                    stage,
                    certExpires,
                    writeStatus: !options.WhatIf);
            }

            _certificateHolder.Set(certResult.Certificate);
            _tokenProvider.Reset();

            if (certResult.IsNearExpiry)
            {
                _logger?.LogWarning(
                    "Certificate {Thumbprint} expires on {Expires:u} (within {Days} days).",
                    settings.CertificateThumbprint,
                    certResult.NotAfterUtc,
                    settings.CertExpiryWarningDays);
            }

            stage = "auth";
            string accessToken;
            try
            {
                accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Finish(
                    SyncExitCode.CertAuth,
                    SyncResultKind.Failed,
                    $"Token acquisition failed: {ex.Message}",
                    user,
                    stage,
                    certExpires,
                    writeStatus: !options.WhatIf);
            }

            stage = "graph";
            var graph = await _graphUserClient.GetUserAsync(upn, settings, accessToken, cancellationToken)
                .ConfigureAwait(false);
            if (!graph.Success || graph.User is null)
            {
                return Finish(
                    SyncExitCode.Graph,
                    SyncResultKind.Failed,
                    graph.Error ?? "Graph lookup failed.",
                    user,
                    stage,
                    certExpires,
                    writeStatus: !options.WhatIf);
            }

            stage = "udf";
            var values = new Dictionary<int, string?>();
            foreach (var mapping in settings.FieldMappings)
            {
                var raw = AttributePathResolver.GetValue(graph.User.Value, mapping.AttributePath);
                values[mapping.UdfNumber] = raw;
                _logger?.LogDebug(
                    "Mapped {Path} -> Custom{Udf} raw='{Raw}'",
                    mapping.AttributePath,
                    mapping.UdfNumber,
                    ValueNormalizer.Normalize(raw, settings.MaxFieldLength));
            }

            var write = _udfWriter.Write(settings, values, options.WhatIf);
            if (!write.Success)
            {
                return Finish(
                    SyncExitCode.WritingUdfs,
                    SyncResultKind.Failed,
                    write.Error ?? "Writing UDFs failed.",
                    user,
                    stage,
                    certExpires,
                    writeStatus: !options.WhatIf);
            }

            var summary = string.Join(
                "; ",
                write.Changes.Select(c => $"Custom{c.UdfNumber}:{c.Action}"));
            return Finish(
                SyncExitCode.Ok,
                SyncResultKind.Success,
                options.WhatIf ? $"WHATIF {summary}" : summary,
                user,
                stage,
                certExpires,
                writeStatus: !options.WhatIf);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Sync failed at stage {Stage}", stage);
            return Finish(
                SyncExitCode.Config,
                SyncResultKind.Failed,
                ex.Message,
                user,
                stage,
                certExpires,
                writeStatus: !options.WhatIf);
        }
    }

    private SyncRunResult Finish(
        SyncExitCode exitCode,
        SyncResultKind kind,
        string? message,
        string? user,
        string stage,
        DateTimeOffset? certExpires,
        bool writeStatus)
    {
        if (kind == SyncResultKind.Failed)
            _logger?.LogError("Sync {Kind} at {Stage}: {Message}", kind, stage, message);
        else if (kind == SyncResultKind.Skipped)
            _logger?.LogInformation("Sync skipped at {Stage}: {Message}", stage, message);
        else
            _logger?.LogInformation("Sync succeeded: {Message}", message);

        if (writeStatus)
        {
            try
            {
                _statusReporter.Write(new SyncStatus
                {
                    LastRunUtc = _clock.UtcNow,
                    LastResult = kind,
                    LastError = kind == SyncResultKind.Failed ? message : null,
                    LastUser = user,
                    LastStage = stage,
                    CertExpiresUtc = certExpires,
                    Version = _version
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write status registry values.");
            }
        }

        return new SyncRunResult
        {
            ExitCode = exitCode,
            Kind = kind,
            Message = message,
            User = user,
            Stage = stage,
            CertExpiresUtc = certExpires
        };
    }
}
