using System.Net;
using System.Net.Http;
using System.Text;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class CodexUsageClientTests
{
    [Fact]
    public async Task ParsesWindowsPlanAndAdditionalRateLimits()
    {
        const string json = """
        {
          "plan_type": "pro",
          "rate_limit": {
            "primary_window": { "used_percent": 38, "reset_at": 1790000000, "limit_window_seconds": 18000 },
            "secondary_window": { "used_percent": 12.5, "reset_at": 1790604800, "limit_window_seconds": 604800 }
          },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_spark",
              "rate_limit": {
                "primary_window": { "used_percent": 20, "reset_at": 1790001000, "limit_window_seconds": 18000 }
              }
            }
          ]
        }
        """;
        var handler = new StubHandler(json);
        var client = new CodexUsageClient(handler, accountIdProvider: () => "account-id");

        var snapshot = await client.FetchUsageAsync("test-token", CancellationToken.None);

        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(38, snapshot.PrimaryUsedPercent);
        Assert.Equal(12.5, snapshot.SecondaryUsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), snapshot.PrimaryResetAt);
        Assert.Single(snapshot.AdditionalRateLimits);
        Assert.Equal("GPT-5.3-Codex-Spark", snapshot.AdditionalRateLimits[0].Name);
        Assert.Equal(20, snapshot.AdditionalRateLimits[0].PrimaryWindow!.UsedPercent);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("Bearer test-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", handler.Request.RequestUri!.ToString());
        Assert.Equal("account-id", handler.Request.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("codex-cli", handler.Request.Headers.UserAgent.Single().Product!.Name);
    }

    [Fact]
    public async Task RejectsUnauthorizedResponses()
    {
        var handler = new StubHandler("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized);
        var client = new CodexUsageClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchUsageAsync("secret-token", CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
