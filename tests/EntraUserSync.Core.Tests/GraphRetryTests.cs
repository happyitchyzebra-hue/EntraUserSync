using System.Net;
using EntraUserSync.Core.Configuration;
using EntraUserSync.Core.Graph;

namespace EntraUserSync.Core.Tests;

public class GraphRetryTests
{
    [Fact]
    public async Task GetUserAsync_RetriesOn429_ThenSucceeds()
    {
        var handler = new FakeHandler([
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1)) }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"1","userPrincipalName":"u@contoso.com","department":"IT"}""")
            }
        ]);

        var client = new GraphUserClient(new HttpClient(handler), maxRetries: 3);
        var settings = new AppSettings
        {
            Cloud = AzureCloud.Global,
            GraphApiVersion = GraphApiVersion.V1_0,
            FieldMappings = [new FieldMapping { AttributePath = "department", UdfNumber = 2 }]
        };

        var result = await client.GetUserAsync("u@contoso.com", settings, "token");
        Assert.True(result.Success);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetUserAsync_Maps403()
    {
        var handler = new FakeHandler([
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("denied")
            }
        ]);
        var client = new GraphUserClient(new HttpClient(handler), maxRetries: 0);
        var result = await client.GetUserAsync("u@contoso.com", new AppSettings
        {
            FieldMappings = [new FieldMapping { AttributePath = "department", UdfNumber = 2 }]
        }, "token");

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("User.Read.All", result.Error);
    }

    [Fact]
    public async Task GetUserAsync_Maps404()
    {
        var handler = new FakeHandler([
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing")
            }
        ]);
        var client = new GraphUserClient(new HttpClient(handler), maxRetries: 0);
        var result = await client.GetUserAsync("missing@contoso.com", new AppSettings
        {
            FieldMappings = [new FieldMapping { AttributePath = "department", UdfNumber = 2 }]
        }, "token");

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void ShouldRetry_Codes(HttpStatusCode code, bool expected)
    {
        Assert.Equal(expected, GraphUserClient.ShouldRetry(code));
    }

    [Fact]
    public void GetRetryDelay_HonoursRetryAfter()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), GraphUserClient.GetRetryDelay(response, 0));
    }

    private sealed class FakeHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public FakeHandler(IEnumerable<HttpResponseMessage> responses)
            : this(new Queue<HttpResponseMessage>(responses))
        {
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(responses.Dequeue());
        }
    }
}
