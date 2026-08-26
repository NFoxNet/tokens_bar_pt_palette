using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageSnapshotCacheTests
{
    [Fact]
    public async Task SharesOneInFlightRefreshBetweenCallers()
    {
        var provider = new BlockingProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        var first = cache.GetUsageSnapshotAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.GetUsageSnapshotAsync();
        provider.Release.TrySetResult();

        var snapshots = await Task.WhenAll(first, second);

        Assert.Equal(1, provider.CallCount);
        Assert.Same(snapshots[0], snapshots[1]);
        Assert.NotNull(snapshots[0].FetchedAt);
    }

    [Fact]
    public async Task InvalidatesCachedSnapshotWhenRefreshSettingsChange()
    {
        var provider = new CountingProvider();
        var settings = new TestRefreshSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        await cache.GetUsageSnapshotAsync();
        await cache.GetUsageSnapshotAsync();
        settings.RaiseChanged();
        await cache.GetUsageSnapshotAsync();

        Assert.Equal(2, provider.CallCount);
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("blocking", "Blocking");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return CreateSnapshot(Descriptor);
        }
    }

    private sealed class CountingProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("counting", "Counting");
        public int CallCount { get; private set; }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CreateSnapshot(Descriptor));
        }
    }

    private sealed class TestRefreshSettings(TimeSpan refreshInterval) : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval { get; } = refreshInterval;
        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static UsageSnapshot CreateSnapshot(UsageProviderDescriptor descriptor)
        => new(
            descriptor.Id,
            descriptor.DisplayName,
            new UsageWindow(1, DateTimeOffset.UtcNow.AddHours(1), 3600),
            new UsageWindow(2, DateTimeOffset.UtcNow.AddDays(1), 86400),
            null,
            false);
}
