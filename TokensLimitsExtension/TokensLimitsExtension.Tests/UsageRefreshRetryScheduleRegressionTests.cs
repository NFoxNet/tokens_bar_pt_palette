using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageRefreshRetryScheduleRegressionTests
{
    [Fact]
    public async Task IntervalChangeAndEarlyTimerTickPreserveRetryAfterAndRetryAutomatically()
    {
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var settings = new TestSettings(TimeSpan.FromMinutes(1));
        var provider = new RateLimitedProvider();
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);
        var retryDeadlinePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.StateChanged += (_, _) =>
        {
            if (cache.State.RetryAfterUntil is not null)
            {
                retryDeadlinePublished.TrySetResult();
            }
        };

        coordinator.UpdateProviders([cache]);
        await provider.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await retryDeadlinePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        settings.SetRefreshInterval(TimeSpan.FromSeconds(20));
        timer.Fire();
        Assert.Equal(TimeSpan.FromSeconds(120), timer.DueTime);

        time.Advance(TimeSpan.FromSeconds(119));
        timer.Fire();
        Assert.Equal(TimeSpan.FromSeconds(1), timer.DueTime);

        time.Advance(TimeSpan.FromSeconds(1));
        timer.Fire();
        await provider.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task AuthenticationStateWithRetryDeadlineRemainsScheduled()
    {
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var settings = new TestSettings(TimeSpan.FromMinutes(1));
        var provider = new DeadlineStateSource(start.AddSeconds(120));
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([provider]);
        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(120), timer.DueTime);
        Assert.Equal(0, provider.CallCount);

        time.Advance(TimeSpan.FromSeconds(120));
        timer.Fire();
        await provider.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(TimeSpan.FromMinutes(1), timer.DueTime);
    }

    [Fact]
    public async Task CacheClearAllowsSameProviderToRefreshDuringPreviousCooldown()
    {
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        var settings = new TestSettings(TimeSpan.FromMinutes(1));
        var provider = new RateLimitedProvider();
        using var cache = new UsageSnapshotCache(provider, settings, time);
        using var coordinator = new UsageRefreshCoordinator(settings, time);

        coordinator.UpdateProviders([cache]);
        await provider.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var timer = await time.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(120), timer.DueTime);

        cache.Clear();
        await coordinator.RefreshProviderAsync(cache);

        await provider.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.CallCount);
    }

    private sealed class TestSettings(TimeSpan refreshInterval) : IUsageRefreshSettings
    {
        public TimeSpan RefreshInterval { get; private set; } = refreshInterval;
        public event EventHandler? Changed;

        public void SetRefreshInterval(TimeSpan interval)
        {
            RefreshInterval = interval;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public TaskCompletionSource<TestTimer> TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class TestTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Fire() => callback(state);
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RateLimitedProvider : IUsageProvider
    {
        private int _callCount;
        public UsageProviderDescriptor Descriptor { get; } = new("scheduled-429", "Scheduled 429");
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var callCount = Interlocked.Increment(ref _callCount);
            (callCount == 1 ? FirstCall : SecondCall).TrySetResult();
            return Task.FromException<UsageSnapshot>(new UsageProviderRequestException(
                "HTTP 429",
                retryAfter: TimeSpan.FromSeconds(120),
                statusCode: System.Net.HttpStatusCode.TooManyRequests));
        }
    }

    private sealed class DeadlineStateSource(DateTimeOffset retryAfterUntil) : IUsageProviderStateSource
    {
        public UsageProviderDescriptor Descriptor { get; } = new("auth-with-deadline", "Auth with deadline");
        public UsageProviderState State { get; private set; } = new(
            null,
            null,
            null,
            false,
            UsageProviderErrorKind.Authentication)
        {
            RetryAfterUntil = retryAfterUntil,
        };
        public event EventHandler? StateChanged { add { } remove { } }
        public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromException<UsageSnapshot>(new InvalidOperationException("No snapshot expected."));

        public bool TryGetSnapshot(out UsageSnapshot snapshot)
        {
            snapshot = null!;
            return false;
        }

        public void Invalidate() { }

        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            CallCount++;
            RefreshStarted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
