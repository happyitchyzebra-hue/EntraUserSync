using EntraUserSync.Core.Configuration;

namespace EntraUserSync.Core.Abstractions;

public sealed class UdfWriteChange
{
    public required int UdfNumber { get; init; }
    public string? PreviousValue { get; init; }
    public string? NewValue { get; init; }
    public required string Action { get; init; }
}

public sealed class UdfWriteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<UdfWriteChange> Changes { get; init; } = [];
}

public interface IUdfWriter
{
    UdfWriteResult Write(
        AppSettings settings,
        IReadOnlyDictionary<int, string?> valuesByUdf,
        bool whatIf);
}
