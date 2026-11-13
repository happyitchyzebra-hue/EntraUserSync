namespace EntraUserSync.Core.Abstractions;

public sealed class SyncStatus
{
    public required DateTimeOffset LastRunUtc { get; init; }
    public required SyncResultKind LastResult { get; init; }
    public string? LastError { get; init; }
    public string? LastUser { get; init; }
    public string? LastStage { get; init; }
    public DateTimeOffset? CertExpiresUtc { get; init; }
    public required string Version { get; init; }
}

public interface IStatusReporter
{
    void Write(SyncStatus status);
}
