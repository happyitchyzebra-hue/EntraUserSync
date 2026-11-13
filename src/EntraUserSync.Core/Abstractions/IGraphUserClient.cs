using System.Text.Json;
using EntraUserSync.Core.Configuration;

namespace EntraUserSync.Core.Abstractions;

public sealed class GraphUserResult
{
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string? Error { get; init; }
    public JsonElement? User { get; init; }
}

public interface IGraphUserClient
{
    Task<GraphUserResult> GetUserAsync(
        string upn,
        AppSettings settings,
        string accessToken,
        CancellationToken cancellationToken = default);
}
