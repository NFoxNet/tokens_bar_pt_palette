using System.Net;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class RefreshCooldownRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorBlocksManualRefreshUntilRetryAfterExpires(bool force)
    {
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(start);
        var provider = new RateLimitedProvider();
        var settings = new TestSettings();
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([cache]);
        await provider.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(start.AddSeconds(120), cache.State.RetryAfterUntil);
        await coordinator.RefreshProviderAsync(cache, force);
        Assert.Equal(1, provider.CallCount);

        time.SetUtcNow(start.AddSeconds(120));
        await coordinator.RefreshProviderAsync(cache, force);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task CacheRetainsLastSnapshotAndPublishesAbsoluteRetryExpiry()
    {
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(start);
        var provider = new SnapshotThenRateLimitedProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);

        await cache.RefreshAsync();
        var snapshot = cache.State.Snapshot;
        time.SetUtcNow(start.AddSeconds(10));
        await cache.RefreshAsync(force: true);

        Assert.Same(snapshot, cache.State.Snapshot);
        Assert.Equal(start.AddSeconds(130), cache.State.RetryAfterUntil);
        Assert.Equal(start, cache.State.LastSuccessfulRefreshAt);

        time.SetUtcNow(start.AddSeconds(130));
        await cache.RefreshAsync(force: true);
        Assert.Null(cache.State.RetryAfterUntil);
    }

    private sealed class TestSettings : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval => TimeSpan.FromMinutes(1);
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }

    private sealed class RateLimitedProvider : IUsageProvider
    {
        private int _callCount;
        public UsageProviderDescriptor Descriptor { get; } = new("cooldown", "Cooldown");
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            FirstCall.TrySetResult();
            return Task.FromException<UsageSnapshot>(new UsageProviderRequestException(
                "HTTP 429",
                retryAfter: TimeSpan.FromSeconds(120),
                statusCode: HttpStatusCode.TooManyRequests));
        }
    }

    private sealed class SnapshotThenRateLimitedProvider : IUsageProvider
    {
        private int _callCount;
        public UsageProviderDescriptor Descriptor { get; } = new("snapshot-cooldown", "Snapshot cooldown");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var callCount = Interlocked.Increment(ref _callCount);
            if (callCount == 1 || callCount > 2)
            {
                return Task.FromResult(new UsageSnapshot(
                    Descriptor.Id,
                    Descriptor.DisplayName,
                    new UsageWindow(callCount, DateTimeOffset.UtcNow.AddHours(1), 3600),
                    null,
                    null,
                    false));
            }

            return Task.FromException<UsageSnapshot>(new UsageProviderRequestException(
                "HTTP 429",
                retryAfter: TimeSpan.FromSeconds(120),
                statusCode: HttpStatusCode.TooManyRequests));
        }
    }
}
