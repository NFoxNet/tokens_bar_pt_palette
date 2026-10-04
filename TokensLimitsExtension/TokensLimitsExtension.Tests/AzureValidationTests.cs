using System.Net;
using System.Text;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Tests;

public sealed class AzureValidationTests
{
    [Fact]
    public async Task AutomaticRefreshReportsQuotaUnavailableWithoutSendingValidationRequest()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient);

        var snapshot = await provider.GetUsageSnapshotAsync();

        Assert.Empty(handler.Requests);
        Assert.Null(snapshot.PrimaryWindow);
        Assert.Null(snapshot.SecondaryWindow);
        Assert.Equal("metadata", snapshot.Source);
        Assert.Contains(snapshot.Metrics, metric =>
            metric.SemanticKey == "quotaUnavailable" && metric.Value == "Unavailable");
    }

    [Fact]
    public async Task ManualValidationSendsOneBoundedAzureRequestAndReturnsOnlyConnectionStatus()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient);

        var result = await Assert.IsAssignableFrom<IUsageProviderConnectionValidator>(provider)
            .ValidateConnectionAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/openai/deployments/test-deployment/chat/completions", request.Uri.AbsolutePath);
        Assert.Equal("2024-10-21", request.Uri.Query.TrimStart('?').Split('=', 2)[1]);
        Assert.Equal("test-key", request.ApiKey);
        Assert.Contains("\"max_tokens\":1", request.Body, StringComparison.Ordinal);
        Assert.Null(result.PrimaryWindow);
        Assert.Null(result.SecondaryWindow);
        Assert.Equal("manual connection validation", result.Source);
        Assert.False(result.IsEstimate);
        Assert.Equal("validated", Assert.Single(result.Metrics).Value);
        Assert.Equal("connectionStatus", result.Metrics[0].SemanticKey);
    }

    [Fact]
    public async Task ManualValidationPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient);

        var validation = Assert.IsAssignableFrom<IUsageProviderConnectionValidator>(provider)
            .ValidateConnectionAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
    }

    [Fact]
    public async Task ManualValidationReportsItsDeadlineAsTimeout()
    {
        var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler);
        using var provider = CreateProvider(httpClient, TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<UsageProviderRequestException>(() =>
            Assert.IsAssignableFrom<IUsageProviderConnectionValidator>(provider)
                .ValidateConnectionAsync());

        Assert.Equal(UsageProviderFailureKind.Timeout, exception.FailureKind);
    }

    private static ConfiguredUsageProvider CreateProvider(HttpClient httpClient, TimeSpan? requestTimeout = null)
        => new(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "azureopenai"),
            new TestConfiguration(),
            httpClient,
            logger: null,
            requestTimeout: requestTimeout ?? TimeSpan.FromSeconds(2),
            maxResponseBodyBytes: 1_024);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestDetails> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestDetails(
                request.Method,
                request.RequestUri!,
                request.Headers.GetValues("api-key").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record RequestDetails(HttpMethod Method, Uri Uri, string ApiKey, string Body);

    private sealed class TestConfiguration : IUsageProviderConfiguration
    {
        public bool IsEnabled(string providerId) => true;

        public string? GetValue(string providerId, string key)
            => key switch
            {
                "apiKey" => "test-key",
                "baseUrl" => "https://example.openai.azure.com",
                "deploymentName" => "test-deployment",
                "apiVersion" => "2024-10-21",
                _ => null,
            };
    }
}
