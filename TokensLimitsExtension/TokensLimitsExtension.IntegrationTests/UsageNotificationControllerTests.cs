using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Notifications;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class UsageNotificationControllerTests
{
    [Fact]
    public async Task ObservesOnlySuccessfulCacheSnapshotsAndStopsAfterDispose()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var provider = new MutableProvider(Snapshot(80));
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);
        var delivery = new RecordingDelivery();
        var controller = new UsageNotificationController(
            [cache],
            new UsageNotificationEvaluator(),
            delivery,
            _ => "process-config-1",
            timeProvider: time);

        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(92);
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Single(delivery.Events);
        Assert.Equal(UsageNotificationEventKind.ThresholdReached, delivery.Events[0].Kind);

        controller.Dispose();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(98);
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Single(delivery.Events);
    }

    [Fact]
    public async Task DisabledControllerDoesNotSubscribeOrDeliver()
    {
        var provider = new MutableProvider(Snapshot(80));
        using var cache = new UsageSnapshotCache(provider);
        var delivery = new RecordingDelivery();
        using var controller = new UsageNotificationController(
            [cache],
            new UsageNotificationEvaluator(),
            delivery,
            _ => "process-config-1",
            enabled: false);

        await cache.RefreshAsync();

        Assert.Empty(delivery.Events);
    }

    [Fact]
    public async Task FailedRefreshWithLastKnownSnapshotDoesNotNotify()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var provider = new MutableProvider(Snapshot(80));
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);
        var delivery = new RecordingDelivery();
        using var controller = new UsageNotificationController(
            [cache],
            new UsageNotificationEvaluator(),
            delivery,
            _ => "process-config-1",
            timeProvider: time);

        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Failure = true;
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Equal(UsageProviderErrorKind.Unknown, cache.State.ErrorKind);
        Assert.Empty(delivery.Events);

        time.Advance(TimeSpan.FromMinutes(1));
        provider.Failure = false;
        provider.Snapshot = Snapshot(92);
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Single(delivery.Events);
    }

    [Fact]
    public async Task FreshEstimateBreaksControllerBaselineWithoutNotification()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var provider = new MutableProvider(Snapshot(80));
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);
        var delivery = new RecordingDelivery();
        using var controller = new UsageNotificationController(
            [cache], new UsageNotificationEvaluator(), delivery, _ => "process-config-1", timeProvider: time);

        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(95) with { IsEstimate = true, Source = "local" };
        cache.Invalidate();
        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(95) with { Source = "api" };
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Empty(delivery.Events);

        time.Advance(TimeSpan.FromSeconds(1));
        provider.Snapshot = Snapshot(80) with { Source = "api" };
        cache.Invalidate();
        await cache.RefreshAsync();

        time.Advance(TimeSpan.FromSeconds(1));
        provider.Snapshot = Snapshot(92) with { Source = "api" };
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Single(delivery.Events);
        Assert.Equal(UsageNotificationEventKind.ThresholdReached, delivery.Events[0].Kind);
    }

    [Fact]
    public async Task UpdateProvidersDoesNotSubscribeIfDisposedDuringEnumeration()
    {
        using var cache = new UsageSnapshotCache(new MutableProvider(Snapshot(80)));
        using var controller = new UsageNotificationController(
            [], new UsageNotificationEvaluator(), new RecordingDelivery(), _ => "process-config-1");
        var caches = new BlockingCacheList(cache);

        var update = Task.Run(() => controller.UpdateProviders(caches));
        Assert.True(caches.EnumerationStarted.Wait(TimeSpan.FromSeconds(5)));
        controller.Dispose();
        caches.ContinueEnumeration.Set();
        await update;

        var stateChanged = typeof(UsageSnapshotCache).GetField("StateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.Null(stateChanged?.GetValue(cache));
    }

    [Fact]
    public async Task StopsDeliveringQueuedWindowNotificationsAfterDispose()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var provider = new MutableProvider(Snapshot(80) with
        {
            SecondaryWindow = new UsageWindow(80, time.GetUtcNow().AddHours(1), 3600),
        });
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);
        var delivery = new RecordingDelivery();
        UsageNotificationController? controller = null;
        controller = new UsageNotificationController(
            [cache],
            new UsageNotificationEvaluator(),
            delivery,
            _ => "process-config-1",
            timeProvider: time);
        delivery.AfterDeliver = _ => controller?.Dispose();

        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(92) with
        {
            SecondaryWindow = new UsageWindow(92, time.GetUtcNow().AddHours(1), 3600),
        };
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Single(delivery.Events);
        Assert.Equal(UsageNotificationWindow.Primary, delivery.Events[0].Window);
    }

    [Fact]
    public async Task ReportsExpectedPlatformDeliveryFailureWithoutExposingExceptionMessage()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var provider = new MutableProvider(Snapshot(80));
        using var cache = new UsageSnapshotCache(provider, timeProvider: time);
        var delivery = new FailingDelivery();
        using var controller = new UsageNotificationController(
            [cache],
            new UsageNotificationEvaluator(),
            delivery,
            _ => "process-config-1",
            timeProvider: time);
        UsageNotificationDeliveryFailedEventArgs? failure = null;
        controller.DeliveryFailed += (_, args) => failure = args;

        await cache.RefreshAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        provider.Snapshot = Snapshot(92);
        cache.Invalidate();
        await cache.RefreshAsync();

        Assert.Equal("test", failure?.ProviderId);
        Assert.Equal(nameof(InvalidOperationException), failure?.ErrorType);
        Assert.Equal(UsageProviderErrorKind.None, cache.State.ErrorKind);
    }

    private static UsageSnapshot Snapshot(double usedPercent)
        => new("test", "Test provider", new UsageWindow(usedPercent, DateTimeOffset.UtcNow.AddHours(1), 3600), null, null, false);

    private sealed class MutableProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("test", "Test provider");

        public UsageSnapshot Snapshot { get; set; } = snapshot;

        public bool Failure { get; set; }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Failure
                ? Task.FromException<UsageSnapshot>(new InvalidOperationException("synthetic provider failure"))
                : Task.FromResult(Snapshot);
    }

    private sealed class RecordingDelivery : IUsageNotificationDelivery
    {
        public List<UsageNotificationEvent> Events { get; } = [];

        public Action<UsageNotificationEvent>? AfterDeliver { get; set; }

        public void Deliver(UsageNotificationEvent notification)
        {
            Events.Add(notification);
            AfterDeliver?.Invoke(notification);
        }
    }

    private sealed class FailingDelivery : IUsageNotificationDelivery
    {
        public void Deliver(UsageNotificationEvent notification)
            => throw new InvalidOperationException("secret must not be surfaced");
    }

    private sealed class BlockingCacheList(UsageSnapshotCache cache) : IReadOnlyList<UsageSnapshotCache>
    {
        public ManualResetEventSlim EnumerationStarted { get; } = new();
        public ManualResetEventSlim ContinueEnumeration { get; } = new();

        public int Count => 1;
        public UsageSnapshotCache this[int index] => cache;

        public IEnumerator<UsageSnapshotCache> GetEnumerator()
        {
            EnumerationStarted.Set();
            if (!ContinueEnumeration.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release provider enumeration.");
            }
            yield return cache;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
