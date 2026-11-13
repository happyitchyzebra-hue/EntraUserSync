using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EntraUserSync.Core.Graph;

public sealed class GraphUserClient : IGraphUserClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphUserClient>? _logger;
    private readonly int _maxRetries;

    public GraphUserClient(HttpClient httpClient, ILogger<GraphUserClient>? logger = null, int maxRetries = 5)
    {
        _httpClient = httpClient;
        _logger = logger;
        _maxRetries = maxRetries;
    }

    public async Task<GraphUserResult> GetUserAsync(
        string upn,
        AppSettings settings,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var (_, graphRoot, _) = CloudEndpoints.Resolve(settings.Cloud);
        var version = CloudEndpoints.GraphVersionPath(settings.GraphApiVersion);
        var paths = settings.FieldMappings.Select(m => m.AttributePath).ToList();
        var select = string.Join(',', AttributePathResolver.GetTopLevelSelectProperties(paths));
        var encodedUpn = Uri.EscapeDataString(upn);
        var url = $"{graphRoot}/{version}/users/{encodedUpn}?$select={select}";

        if (AttributePathResolver.NeedsManagerExpand(paths))
        {
            url += "&$expand=manager($select=displayName,mail,userPrincipalName,jobTitle)";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new GraphUserResult
                {
                    Success = false,
                    StatusCode = 403,
                    Error = "Graph returned 403 Forbidden. Ensure the app has User.Read.All application permission with admin consent."
                };
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new GraphUserResult
                {
                    Success = false,
                    StatusCode = 404,
                    Error = $"User '{upn}' was not found in the tenant (Graph 404)."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new GraphUserResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Error = $"Graph request failed with {(int)response.StatusCode}: {TrimBody(body)}"
                };
            }

            using var doc = JsonDocument.Parse(body);
            return new GraphUserResult
            {
                Success = true,
                StatusCode = (int)response.StatusCode,
                User = doc.RootElement.Clone()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Graph lookup failed for {Upn}", upn);
            return new GraphUserResult { Success = false, Error = ex.Message };
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage prototype,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            using var request = await CloneAsync(prototype).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode) || attempt == _maxRetries)
                    return response;

                var delay = GetRetryDelay(response, attempt);
                _logger?.LogWarning(
                    "Graph returned {Status}. Retrying in {Delay}s (attempt {Attempt}/{Max}).",
                    (int)response.StatusCode,
                    delay.TotalSeconds,
                    attempt + 1,
                    _maxRetries);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger?.LogWarning(ex, "Graph transport error. Retrying in {Delay}s.", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastException ?? new InvalidOperationException("Graph request failed after retries.");
    }

    internal static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    internal static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delta;

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
                return until;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        clone.Version = request.Version;
        return clone;
    }

    private static string TrimBody(string body) =>
        body.Length <= 500 ? body : body[..500] + "...";
}
