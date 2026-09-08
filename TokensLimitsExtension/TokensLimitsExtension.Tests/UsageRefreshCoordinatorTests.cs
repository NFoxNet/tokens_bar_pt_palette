using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageRefreshCoordinatorTests
{
    [Fact]
    public async Task CancelsRequestForProviderRemovedFromSchedule()
    {
        var provider = new BlockingProvider();
        using var cache = new UsageSnapshotCache(provider);
        var settings = new TestSettings();
        using var coordinator = new UsageRefreshCoordinator(settings);

        coordinator.UpdateProviders([cache]);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.UpdateProviders([]);

        await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DoesNotRefreshAProviderRemovedFromTheSchedule()
    {
        var provider = new CountingProvider();
        var settings = new TestSettings();
        using var cache = new UsageSnapshotCache(provider, settings);
        using var coordinator = new UsageRefreshCoordinator(settings);

        coordinator.UpdateProviders([cache]);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.UpdateProviders([]);
        var callsBeforeRefresh = provider.CallCount;
        coordinator.RefreshAll();

        Assert.Equal(callsBeforeRefresh, provider.CallCount);
    }

    private sealed class TestSettings(TimeSpan? refreshInterval = null) : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval => refreshInterval ?? TimeSpan.FromHours(1);
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class BlockingProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("blocking", "Blocking");
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class CountingProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("counting", "Counting");

        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            return Task.FromResult(new UsageSnapshot(
                Descriptor.Id,
                Descriptor.DisplayName,
                new UsageWindow(1, DateTimeOffset.UtcNow.AddHours(1), 3600),
                null,
                null,
                false));
        }
    }
}
