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

    private static UsageSnapshot Snapshot(double usedPercent)
        => new("test", "Test provider", new UsageWindow(usedPercent, DateTimeOffset.UtcNow.AddHours(1), 3600), null, null, false);

    private sealed class MutableProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("test", "Test provider");

        public UsageSnapshot Snapshot { get; set; } = snapshot;

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);
    }

    private sealed class RecordingDelivery : IUsageNotificationDelivery
    {
        public List<UsageNotificationEvent> Events { get; } = [];

        public void Deliver(UsageNotificationEvent notification) => Events.Add(notification);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
