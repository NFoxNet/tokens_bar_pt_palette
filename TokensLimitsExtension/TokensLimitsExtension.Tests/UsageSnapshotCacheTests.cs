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

    [Fact]
    public async Task DoesNotCacheResponseStartedBeforeInvalidation()
    {
        var provider = new InvalidationDuringRefreshProvider();
        var settings = new TestRefreshSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        var refresh = cache.GetUsageSnapshotAsync();
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.RaiseChanged();
        provider.ReleaseFirstCall.TrySetResult();

        var snapshot = await refresh;

        Assert.Equal(2, provider.CallCount);
        Assert.Equal("2", snapshot.Plan);
        var cachedSnapshot = await cache.GetUsageSnapshotAsync();
        Assert.Same(snapshot, cachedSnapshot);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task CancelsAnInFlightRefreshWhenDisposed()
    {
        var provider = new BlockingProvider();
        var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        var refresh = cache.GetUsageSnapshotAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task KeepsLastSuccessfulSnapshotWhenRefreshFails()
    {
        var provider = new SucceedsThenFailsProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        await cache.RefreshAsync();
        await cache.RefreshAsync(force: true);

        Assert.True(cache.TryGetSnapshot(out var snapshot));
        Assert.Equal("first", snapshot.Plan);
        Assert.Equal(UsageProviderErrorKind.Network, cache.State.ErrorKind);
        Assert.True(cache.State.IsStale);
    }

    [Fact]
    public async Task LanguageStyleChangeDoesNotInvalidateButConfigurationChangeClearsSnapshot()
    {
        var provider = new CountingProvider();
        var settings = new ConfigurationAwareSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        await cache.GetUsageSnapshotAsync();
        settings.RaiseGeneralChanged();
        await cache.GetUsageSnapshotAsync();

        Assert.Equal(1, provider.CallCount);
        settings.RaiseProviderConfigurationChanged();
        Assert.False(cache.TryGetSnapshot(out _));
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

    private sealed class InvalidationDuringRefreshProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("invalidation", "Invalidation");
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            if (callNumber == 1)
            {
                FirstCallStarted.TrySetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }

            return CreateSnapshot(Descriptor) with
            {
                Plan = callNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }
    }

    private sealed class SucceedsThenFailsProvider : IUsageProvider
    {
        private int _callCount;
        public UsageProviderDescriptor Descriptor { get; } = new("flaky", "Flaky");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return Task.FromResult(CreateSnapshot(Descriptor) with { Plan = "first" });
            }

            throw new HttpRequestException("connection failed");
        }
    }

    private sealed class TestRefreshSettings(TimeSpan refreshInterval) : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval { get; } = refreshInterval;
        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ConfigurationAwareSettings(TimeSpan refreshInterval) : IUsageRefreshSettings, IUsageProviderConfigurationChangeSource
    {
        public TimeSpan RefreshInterval { get; } = refreshInterval;
        public event EventHandler? Changed;
        public event EventHandler? ProviderConfigurationChanged;

        public void RaiseGeneralChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public void RaiseProviderConfigurationChanged() => ProviderConfigurationChanged?.Invoke(this, EventArgs.Empty);
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
