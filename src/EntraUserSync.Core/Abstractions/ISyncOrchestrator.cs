namespace EntraUserSync.Core.Abstractions;

public sealed class SyncRunOptions
{
    public bool WhatIf { get; init; }
    public string? OverrideUpn { get; init; }
}

public sealed class SyncRunResult
{
    public required SyncExitCode ExitCode { get; init; }
    public required SyncResultKind Kind { get; init; }
    public string? Message { get; init; }
    public string? User { get; init; }
    public string? Stage { get; init; }
    public DateTimeOffset? CertExpiresUtc { get; init; }
}

public interface ISyncOrchestrator
{
    Task<SyncRunResult> RunAsync(SyncRunOptions options, CancellationToken cancellationToken = default);
}
