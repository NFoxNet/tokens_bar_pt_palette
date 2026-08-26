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
        Assert.Contains("/v1/billing/teams/team-123/prepaid/balance", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer management-key", handler.LastRequest.Headers.Authorization!.ToString());
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
