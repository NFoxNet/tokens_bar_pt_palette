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

    private sealed class TestSettings : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
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
}
