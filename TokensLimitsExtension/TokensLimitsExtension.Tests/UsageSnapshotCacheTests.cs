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
        var remaining = Enumerable.Range(0, 19)
            .Select(_ => cache.GetUsageSnapshotAsync())
            .ToArray();
        provider.Release.TrySetResult();

        var snapshots = await Task.WhenAll([first, .. remaining]);

        Assert.Equal(1, provider.CallCount);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
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
    public async Task DoesNotRaiseStateChangedAfterDisposeCancelsAnInFlightRefresh()
    {
        var provider = new BlockingProvider();
        var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());
        var stateChanged = 0;
        cache.StateChanged += (_, _) => Interlocked.Increment(ref stateChanged);

        var refresh = cache.RefreshAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref stateChanged, 0);
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(0, stateChanged);
    }

    [Fact]
    public async Task CancelsThePreviousGenerationWhenProviderConfigurationChanges()
    {
        var provider = new SequentialBlockingProvider();
        var settings = new ConfigurationAwareSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        var previousGeneration = cache.GetUsageSnapshotAsync();
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.RaiseProviderConfigurationChanged(provider.Descriptor.Id);
        var currentGeneration = cache.GetUsageSnapshotAsync();
        await provider.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.ReleaseFirstCall.TrySetResult();
        provider.ReleaseSecondCall.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previousGeneration);
        var snapshot = await currentGeneration;

        Assert.Equal(2, provider.CallCount);
        Assert.Equal("current", snapshot.Plan);
        Assert.True(cache.TryGetSnapshot(out var cachedSnapshot));
        Assert.Same(snapshot, cachedSnapshot);
    }

    [Fact]
    public async Task ConfigurationChangeDoesNotPublishAnErrorFromAnOldRefresh()
    {
        var provider = new SequentialBlockingProvider();
        var settings = new ConfigurationAwareSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        var refresh = cache.RefreshAsync();
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.RaiseProviderConfigurationChanged(provider.Descriptor.Id);
        provider.ReleaseFirstCall.TrySetResult();
        await refresh;

        Assert.Equal(UsageProviderErrorKind.None, cache.State.ErrorKind);
        Assert.False(cache.State.IsRefreshing);
        Assert.False(cache.TryGetSnapshot(out _));
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedRefresh()
    {
        var provider = new BlockingProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());
        using var cancellation = new CancellationTokenSource();

        var cancelledRefresh = cache.GetUsageSnapshotAsync(cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waitingRefresh = cache.GetUsageSnapshotAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRefresh);
        provider.Release.TrySetResult();
        var snapshot = await waitingRefresh.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(snapshot.FetchedAt);
    }

    [Fact]
    public async Task SharesOneFailedRefreshBetweenConcurrentCallers()
    {
        var provider = new BlockingFailureProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        var requests = Enumerable.Range(0, 20)
            .Select(_ => cache.GetUsageSnapshotAsync())
            .ToArray();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Release.TrySetResult();

        foreach (var request in requests)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => request);
        }

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task CoalescesConcurrentForcedRefreshes()
    {
        var provider = new BlockingProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        var refreshes = Enumerable.Range(0, 20)
            .Select(_ => cache.RefreshAsync(force: true))
            .ToArray();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Release.TrySetResult();
        await Task.WhenAll(refreshes);

        Assert.Equal(1, provider.CallCount);
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
    public async Task KeepsStaleErrorVisibleWhileAReplacementRefreshIsRunning()
    {
        var provider = new StaleWhileRefreshingProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        await cache.RefreshAsync();
        await cache.RefreshAsync(force: true);
        var refresh = cache.RefreshAsync(force: true);
        await provider.ThirdCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cache.State.IsRefreshing);
        Assert.True(cache.State.IsStale);
        Assert.Equal(UsageProviderErrorKind.Network, cache.State.ErrorKind);

        provider.ReleaseThirdCall.TrySetResult();
        await refresh;
    }

    [Fact]
    public async Task PreservesTypedRateLimitStatusAndRetryAfter()
    {
        var provider = new RateLimitedProvider();
        using var cache = new UsageSnapshotCache(provider, timeProvider: new FixedTimeProvider());

        await cache.RefreshAsync();

        Assert.Equal(UsageProviderErrorKind.RateLimited, cache.State.ErrorKind);
        Assert.Equal(TimeSpan.FromSeconds(120), cache.State.RetryAfter);
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
        settings.RaiseProviderConfigurationChanged(provider.Descriptor.Id);
        Assert.False(cache.TryGetSnapshot(out _));
        await cache.GetUsageSnapshotAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task IgnoresConfigurationChangesForAnotherProvider()
    {
        var provider = new CountingProvider();
        var settings = new ConfigurationAwareSettings(TimeSpan.FromMinutes(10));
        using var cache = new UsageSnapshotCache(provider, settings, new FixedTimeProvider());

        await cache.GetUsageSnapshotAsync();
        settings.RaiseProviderConfigurationChanged("other-provider");
        await cache.GetUsageSnapshotAsync();

        Assert.Equal(1, provider.CallCount);
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

    private sealed class BlockingFailureProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("failure", "Failure");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("connection failed");
        }
    }

    private sealed class SequentialBlockingProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("sequential", "Sequential");
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            if (callNumber == 1)
            {
                FirstCallStarted.TrySetResult();
                // Simulate an adapter that completes a response despite its
                // cancellation token having been signalled.
                await ReleaseFirstCall.Task;
            }
            else
            {
                SecondCallStarted.TrySetResult();
                await ReleaseSecondCall.Task.WaitAsync(cancellationToken);
            }

            return CreateSnapshot(Descriptor) with { Plan = "current" };
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

    private sealed class StaleWhileRefreshingProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("stale-refresh", "Stale refresh");
        public TaskCompletionSource ThirdCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseThirdCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                return CreateSnapshot(Descriptor) with { Plan = "first" };
            }

            if (call == 2)
            {
                throw new HttpRequestException("connection failed");
            }

            ThirdCallStarted.TrySetResult();
            await ReleaseThirdCall.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("connection failed again");
        }
    }

    private sealed class RateLimitedProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("rate-limited", "Rate limited");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromException<UsageSnapshot>(new UsageProviderRequestException(
                "provider returned an error",
                retryAfter: TimeSpan.FromSeconds(120),
                statusCode: System.Net.HttpStatusCode.TooManyRequests));
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
        public event EventHandler<UsageProviderConfigurationChangedEventArgs>? ProviderConfigurationChanged;

        public void RaiseGeneralChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public void RaiseProviderConfigurationChanged(string providerId)
            => ProviderConfigurationChanged?.Invoke(
                this,
                new UsageProviderConfigurationChangedEventArgs(new HashSet<string>([providerId], StringComparer.OrdinalIgnoreCase)));
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
