namespace EntraUserSync.Core.Abstractions;

public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    void Reset();
}
