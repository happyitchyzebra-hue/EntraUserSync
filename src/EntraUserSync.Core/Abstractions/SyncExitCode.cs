namespace EntraUserSync.Core.Abstractions;

/// <summary>
/// Exit codes aligned with the original PowerShell script contract.
/// </summary>
public enum SyncExitCode
{
    Ok = 0,
    Config = 1,
    UserDetection = 2,
    CertAuth = 3,
    Graph = 4,
    WritingUdfs = 5
}

public enum SyncResultKind
{
    Success,
    Skipped,
    Failed
}
