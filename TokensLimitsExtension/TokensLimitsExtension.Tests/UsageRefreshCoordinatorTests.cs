using System.Net;
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

    [Fact]
    public async Task SchedulesTheNextRefreshFromCompletionWithOneTimer()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var provider = new DelayedProvider();
        var settings = new TestSettings(TimeSpan.FromSeconds(60));
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([cache]);
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(2));
        provider.ReleaseFirstCall.TrySetResult();
        await provider.FirstCallCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), timer.DueTime);

        time.Advance(TimeSpan.FromSeconds(59));
        timer.Fire();
        Assert.Equal(1, provider.CallCount);

        time.Advance(TimeSpan.FromSeconds(1));
        timer.Fire();
        await provider.SecondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RecalculatesTheNearestDeadlineAndDisablesTheTimerWhenEmpty()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var provider = new DelayedProvider();
        var settings = new TestSettings(TimeSpan.FromSeconds(60));
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([cache]);
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.ReleaseFirstCall.TrySetResult();
        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        settings.SetRefreshInterval(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), timer.DueTime);

        coordinator.UpdateProviders([]);
        Assert.Equal(Timeout.InfiniteTimeSpan, timer.DueTime);
    }

    [Fact]
    public async Task UsesRetryAfterBeforeSchedulingAnotherAutomaticRefresh()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var provider = new RateLimitedProvider();
        var settings = new TestSettings(TimeSpan.FromSeconds(60));
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([cache]);

        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(120), timer.DueTime);
    }

    private sealed class TestSettings : IUsageRefreshSettings
    {
        public TestSettings(TimeSpan? refreshInterval = null)
        {
            RefreshInterval = refreshInterval ?? TimeSpan.FromHours(1);
        }

        public TimeSpan RefreshInterval { get; private set; }
        public event EventHandler? Changed;

        public void SetRefreshInterval(TimeSpan interval)
        {
            RefreshInterval = interval;
            Changed?.Invoke(this, EventArgs.Empty);
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

    private sealed class DelayedProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("delayed", "Delayed");
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var callCount = Interlocked.Increment(ref _callCount);
            if (callCount == 1)
            {
                FirstCallStarted.TrySetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
                FirstCallCompleted.TrySetResult();
            }
            else
            {
                SecondCallStarted.TrySetResult();
            }

            return new UsageSnapshot(
                Descriptor.Id,
                Descriptor.DisplayName,
                new UsageWindow(1, DateTimeOffset.UtcNow.AddHours(1), 3600),
                null,
                null,
                false);
        }
    }

    private sealed class RateLimitedProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("rate-limited", "Rate limited");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromException<UsageSnapshot>(new UsageProviderRequestException(
                "HTTP 429",
                retryAfter: TimeSpan.FromSeconds(120),
                statusCode: HttpStatusCode.TooManyRequests));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public TestTimer? Timer { get; private set; }
        public TaskCompletionSource<TestTimer> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new TestTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult(Timer);
            return Timer;
        }

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class TestTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public TaskCompletionSource<TimeSpan> FirstScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                FirstScheduled.TrySetResult(dueTime);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
