using System.Net;
using System.Net.Http;
using System.Text;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Tests;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void MirrorsTheCurrentCodexBarManifest()
    {
        var descriptors = UsageProviderDescriptorRegistry.All;

        Assert.Equal(69, descriptors.Count);
        Assert.Equal(descriptors.Count, descriptors.Select(descriptor => descriptor.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(descriptors.Count, descriptors.Select(descriptor => descriptor.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            descriptors.Where(descriptor => descriptor.Id != "codex"),
            descriptor => Assert.NotEmpty(UsageProviderEndpointCatalog.For(descriptor.Id)));
    }

    [Fact]
    public async Task GenericAdapterParsesRealLimitAndMetricFieldsWithoutEstimatingMissingData()
    {
        var resetAt = DateTimeOffset.UtcNow.AddHours(2);
        var handler = new StubHandler($$"""
            {
              "data": {
                "five_hour": { "used_percent": 15, "reset_at": "{{resetAt:O}}", "window_seconds": 18000 },
                "weekly": { "remaining_percent": 72, "reset_at": "{{resetAt.AddDays(3):O}}", "window_seconds": 604800 },
                "input_tokens": 1200,
                "output_tokens": 340,
                "plan": "team"
              }
            }
            """);
        using var httpClient = new HttpClient(handler);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "openrouter"),
            new TestConfiguration(("openrouter", "apiKey", "test-key")),
            httpClient);

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(15, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(28, snapshot.SecondaryWindow!.UsedPercent);
        Assert.Equal("team", snapshot.Plan);
        Assert.Contains(snapshot.Metrics, metric => metric.Name == "input tokens" && metric.Value == "1200");
        Assert.Equal("Bearer test-key", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task MissingCredentialIsReportedBeforeMakingARequest()
    {
        var handler = new StubHandler("{}");
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "deepseek"),
            new TestConfiguration(),
            new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<UsageProviderRequestException>(() => provider.GetUsageSnapshotAsync());

        Assert.Contains("API-ключ", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ProviderSpecificEndpointUsesConfiguredAccountAndBaseUrl()
    {
        var handler = new StubHandler("{\"total\":{\"val\":\"-1250\"},\"canConsume\":true}");
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "xai"),
            new TestConfiguration(
                ("xai", "apiKey", "management-key"),
                ("xai", "accountId", "team-123"),
                ("xai", "baseUrl", "https://management.example.test")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Contains(snapshot.Metrics, metric => metric.Name == "val" && metric.Value == "-1250");
        Assert.Contains(
            handler.Requests,
            request => request.RequestUri?.PathAndQuery == "/v1/billing/teams/team-123/prepaid/balance");
        Assert.All(handler.Requests, request => Assert.Equal("Bearer management-key", request.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task ClinePassResponseProducesFiveHourAndWeeklyWindows()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var handler = new StubHandler($$"""
            {
              "success": true,
              "data": {
                "limits": [
                  { "type": "five_hour", "percentUsed": 12, "resetsAt": "{{reset:O}}" },
                  { "type": "weekly", "percentUsed": 31, "resetsAt": "{{reset.AddDays(5):O}}" }
                ]
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "clinepass"),
            new TestConfiguration(("clinepass", "apiKey", "cline-key")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(12, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(31, snapshot.SecondaryWindow!.UsedPercent);
        Assert.Equal("Bearer cline-key", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ZaiQuotaResponseMapsUnitAndRemainingValuesToWindows()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds();
        var handler = new StubHandler($$"""
            {
              "success": true,
              "data": {
                "limits": [
                  { "type": "TOKENS_LIMIT", "unit": 5, "number": 300, "usage": 1000, "currentValue": 250, "remaining": 750, "nextResetTime": {{reset}} },
                  { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 4000, "remaining": 6000, "nextResetTime": {{reset + TimeSpan.FromDays(4).TotalMilliseconds}} }
                ]
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "zai"),
            new TestConfiguration(("zai", "apiKey", "zai-key")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(25, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(40, snapshot.SecondaryWindow!.UsedPercent);
    }

    [Fact]
    public async Task QwenGatewayResponseMapsRollingWindows()
    {
        var fiveHourReset = DateTimeOffset.UtcNow.AddHours(2);
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(5);
        var handler = new StubHandler($$"""
            {
              "data": {
                "per5HourPercentage": 0.15,
                "per5HourResetTime": "{{fiveHourReset:O}}",
                "per1WeekPercentage": 0.31,
                "per1WeekResetTime": "{{weeklyReset:O}}"
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "qwencloud"),
            new TestConfiguration(("qwencloud", "cookieHeader", "sec_token=test")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(15, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(31, snapshot.SecondaryWindow!.UsedPercent);
        Assert.Equal("token-plan/usage", snapshot.Source);
    }

    [Fact]
    public async Task StepFunResponseMapsRemainingRatesToUsageWindows()
    {
        var fiveHourReset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(5).ToUnixTimeSeconds();
        var handler = new StubHandler($$"""
            {
              "status": 1,
              "five_hour_usage_left_rate": 0.85,
              "weekly_usage_left_rate": 0.69,
              "five_hour_usage_reset_time": "{{fiveHourReset}}",
              "weekly_usage_reset_time": "{{weeklyReset}}"
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "stepfun"),
            new TestConfiguration(("stepfun", "apiKey", "oasis-token")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(15, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(31, snapshot.SecondaryWindow!.UsedPercent);
        Assert.All(handler.Requests, request => Assert.Equal("oasis-token", request.Headers.GetValues("oasis-token").Single()));
    }

    [Fact]
    public async Task WindsurfResponseMapsDailyAndWeeklyRemainingPercent()
    {
        var dailyReset = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var weeklyReset = DateTimeOffset.UtcNow.AddDays(4).ToUnixTimeSeconds();
        var handler = new StubHandler($$"""
            {
              "planStatus": {
                "dailyQuotaRemainingPercent": 85,
                "weeklyQuotaRemainingPercent": 67,
                "dailyQuotaResetAtUnix": {{dailyReset}},
                "weeklyQuotaResetAtUnix": {{weeklyReset}}
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "windsurf"),
            new TestConfiguration(("windsurf", "cookieHeader", "session=test")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(15, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(33, snapshot.SecondaryWindow!.UsedPercent);
    }

    [Fact]
    public async Task AntigravityResponseUsesMostConstrainedRemainingFraction()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
        var handler = new StubHandler($$"""
            {
              "quotas": [
                { "model": "fast", "remainingFraction": 0.9, "resetTime": "{{reset}}" },
                { "model": "pro", "remainingFraction": 0.6, "resetTime": "{{reset}}" }
              ]
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "antigravity"),
            new TestConfiguration(("antigravity", "oauthToken", "oauth-token")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(40, snapshot.PrimaryWindow!.UsedPercent);
    }

    [Fact]
    public async Task CopilotUsageInheritsQuotaResetDateFromRootEnvelope()
    {
        var reset = DateTimeOffset.UtcNow.AddDays(4);
        var handler = new StubHandler($$"""
            {
              "copilotPlan": "pro",
              "quotaResetDate": "{{reset:O}}",
              "quotaSnapshots": {
                "premiumInteractions": { "percentRemaining": 82, "creditsUsed": 18 },
                "chat": { "percentRemaining": 91, "creditsUsed": 9 }
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "copilot"),
            new TestConfiguration(("copilot", "oauthToken", "github-token")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(18, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(9, snapshot.SecondaryWindow!.UsedPercent);
        Assert.Equal("token github-token", handler.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ClawRouterBudgetLedgerMapsMonthlyUsageAndTokenMetrics()
    {
        var handler = new StubHandler("""
            {
              "budget": {
                "configured": true,
                "ledger": "monthly",
                "limitMicros": 1000000,
                "spentMicros": 250000,
                "remainingMicros": 750000,
                "windowKey": "2026-08"
              },
              "usage": {
                "summary": {
                  "requestCount": 4,
                  "inputTokens": 120,
                  "outputTokens": 80,
                  "totalTokens": 200,
                  "actualCostMicros": 250000
                },
                "providers": []
              }
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "clawrouter"),
            new TestConfiguration(("clawrouter", "apiKey", "claw-key")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Equal(25, snapshot.PrimaryWindow!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), snapshot.PrimaryWindow.ResetAt);
        Assert.Contains(snapshot.Metrics, metric => metric.Name.Equals("input Tokens", StringComparison.OrdinalIgnoreCase) && metric.Value == "120");
    }

    [Fact]
    public async Task OllamaTagsAreShownAsLocalMetricsWithoutInventingQuota()
    {
        var handler = new StubHandler("""
            {
              "models": [
                { "name": "qwen2.5:7b" },
                { "name": "llama3.2:latest" }
              ]
            }
            """);
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "ollama"),
            new TestConfiguration(),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Null(snapshot.PrimaryWindow);
        Assert.Contains(snapshot.Metrics, metric => metric.Name == "Models" && metric.Value == "2");
        Assert.Contains(snapshot.Metrics, metric => metric.Name == "Model 1" && metric.Value == "qwen2.5:7b");
    }

    [Fact]
    public async Task OpenAiUsageUsesAdminKeyAndFollowsUsagePagination()
    {
        var handler = new OpenAiHandler();
        using var provider = new ConfiguredUsageProvider(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "openai"),
            new TestConfiguration(
                ("openai", "adminApiKey", "admin-key"),
                ("openai", "baseUrl", "https://openai.example.test"),
                ("openai", "historyDays", "1")),
            new HttpClient(handler));

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Contains(snapshot.Metrics, metric => metric.Name == "Spend" && metric.Value == "1.25");
        Assert.Contains(snapshot.Metrics, metric => metric.Name == "Tokens" && metric.Value == "120");
        Assert.Contains(snapshot.Metrics, metric => metric.Name == "Model gpt-test" && metric.Value == "120");
        Assert.Equal(4, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer admin-key", request.Headers.Authorization!.ToString());
            Assert.Contains("limit=31", request.RequestUri!.Query, StringComparison.Ordinal);
        });
        Assert.Contains(handler.Requests, request => request.RequestUri!.Query.Contains("page=next", StringComparison.Ordinal));
    }

    private sealed class TestConfiguration(params (string ProviderId, string Key, string Value)[] entries)
        : IUsageProviderConfiguration
    {
        public bool IsEnabled(string providerId) => true;

        public string? GetValue(string providerId, string key)
            => entries.FirstOrDefault(entry =>
                string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }

    private sealed class OpenAiHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var isCosts = request.RequestUri!.AbsolutePath.EndsWith("/costs", StringComparison.Ordinal);
            var hasPage = request.RequestUri.Query.Contains("page=next", StringComparison.Ordinal);
            var body = isCosts
                ? hasPage
                    ? "{\"data\":[],\"has_more\":false}"
                    : "{\"data\":[{\"results\":[{\"amount\":{\"value\":1.25}}]}],\"has_more\":true,\"next_page\":\"next\"}"
                : hasPage
                    ? "{\"data\":[],\"has_more\":false}"
                    : "{\"data\":[{\"results\":[{\"model\":\"gpt-test\",\"input_tokens\":80,\"output_tokens\":40,\"num_model_requests\":2}]}],\"has_more\":true,\"next_page\":\"next\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
