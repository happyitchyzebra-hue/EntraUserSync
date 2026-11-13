namespace EntraUserSync.Core.Abstractions;

public sealed class DetectedUser
{
    public required string Sid { get; init; }
    public required string Upn { get; init; }
    public required string Source { get; init; }
}

public enum UserDetectionOutcome
{
    Found,
    NoUser,
    LocalAccount,
    SuffixNotAllowed,
    AmbiguousSessions,
    Failed
}

public sealed class UserDetectionResult
{
    public required UserDetectionOutcome Outcome { get; init; }
    public DetectedUser? User { get; init; }
    public string? Message { get; init; }
}

public interface IUserDetector
{
    UserDetectionResult Detect(IReadOnlyList<string> allowedUpnSuffixes);
}
