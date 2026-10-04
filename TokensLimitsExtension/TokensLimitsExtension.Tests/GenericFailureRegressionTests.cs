using System.Net;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class GenericFailureRegressionTests
{
    [Fact]
    public async Task ReportsTimeoutWhenApiRequestTimesOutAndOptionalCookieIsMissing()
    {
        using var started = new ManualResetEventSlim();
        using var provider = CreateProvider(new CallbackHandler(async (_, cancellationToken) =>
        {
            started.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }), TimeSpan.FromMilliseconds(100), ("apiKey", "test-key"));
        using var cache = new UsageSnapshotCache(provider);

        var refresh = cache.RefreshAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        await refresh;

        Assert.Equal(UsageProviderErrorKind.Timeout, cache.State.ErrorKind);
        Assert.Null(cache.State.RetryAfter);
    }

    [Fact]
    public async Task ReportsApiFailureWhenDeepSeekCookieEndpointsAreUnavailable()
    {
        var handler = new CallbackHandler((_, _) => Task.FromResult(Response(HttpStatusCode.InternalServerError)));
        using var provider = CreateProvider(handler, TimeSpan.FromSeconds(1), ("apiKey", "test-key"));
        using var cache = new UsageSnapshotCache(provider);

        await cache.RefreshAsync();

        Assert.Equal(UsageProviderErrorKind.UnsupportedResponse, cache.State.ErrorKind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PreservesEarlierRateLimitAndRetryAfterAcrossLaterServerErrors()
    {
        var retryAfter = TimeSpan.FromSeconds(37);
        var handler = new CallbackHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "api.deepseek.com"
                ? Response(HttpStatusCode.TooManyRequests, retryAfter)
                : Response(HttpStatusCode.InternalServerError)));
        using var provider = CreateProvider(
            handler,
            TimeSpan.FromSeconds(1),
            ("apiKey", "test-key"),
            ("cookieHeader", "session=test-cookie"));
        using var cache = new UsageSnapshotCache(provider);

        await cache.RefreshAsync();

        Assert.Equal(UsageProviderErrorKind.RateLimited, cache.State.ErrorKind);
        Assert.Equal(retryAfter, cache.State.RetryAfter);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var started = new ManualResetEventSlim();
        var handler = new CallbackHandler(async (_, cancellationToken) =>
        {
            started.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var provider = CreateProvider(handler, TimeSpan.FromSeconds(10), ("apiKey", "test-key"));
        using var cache = new UsageSnapshotCache(provider);
        using var cancellation = new CancellationTokenSource();

        var refresh = cache.RefreshAsync(cancellationToken: cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(UsageProviderErrorKind.None, cache.State.ErrorKind);
    }

    [Fact]
    public async Task ReportsMissingConfigurationWhenNoCredentialSourceIsAvailable()
    {
        var handler = new CallbackHandler((_, _) => throw new InvalidOperationException("Request should not be sent."));
        using var provider = CreateProvider(handler, TimeSpan.FromSeconds(1));
        using var cache = new UsageSnapshotCache(provider);

        await cache.RefreshAsync();

        Assert.Equal(UsageProviderErrorKind.MissingConfiguration, cache.State.ErrorKind);
        Assert.Empty(handler.Requests);
    }

    private static ConfiguredUsageProvider CreateProvider(
        HttpMessageHandler handler,
        TimeSpan timeout,
        params (string Key, string Value)[] values)
    {
        var descriptor = new UsageProviderDescriptor(
            "deepseek",
            "DeepSeek",
            UsageProviderAuthKind.ApiKey,
            settings: [new UsageProviderSettingDescriptor("apiKey", "API key", "Test key.", IsSecret: true)]);
        var configuration = new TestConfiguration(values);
        return new ConfiguredUsageProvider(
            descriptor,
            configuration,
            new HttpClient(handler),
            logger: null,
            requestTimeout: timeout,
            maxResponseBodyBytes: 4096);
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{}"),
        };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }

        return response;
    }

    private sealed class TestConfiguration((string Key, string Value)[] values) : IUsageProviderConfiguration
    {
        public bool IsEnabled(string providerId) => true;

        public string? GetValue(string providerId, string key)
            => values.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await callback(request, cancellationToken);
        }
    }
}
