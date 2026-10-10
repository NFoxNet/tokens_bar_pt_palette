using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class UsageHistoryControllerTests
{
    [Fact]
    public async Task IgnoresRefreshStartAndDuplicateSnapshots()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(now);
        var provider = new MutableProvider(Snapshot(now, 20));
        using var cache = new UsageSnapshotCache(provider, timeProvider: clock);
        var store = new UsageHistoryStore(directory.Path, timeProvider: clock);
        using var controller = new UsageHistoryController(store, [cache], clock);
        provider.BlockNextRefresh();
        var refresh = cache.RefreshAsync(force: true);
        await provider.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(store.Read("codex"));
        provider.ReleaseRefresh();
        await refresh;
        await cache.RefreshAsync(force: true);

        Assert.Single(store.Read("codex"));
    }

    [Fact]
    public async Task SamplesAtMostEveryFiveMinutesAndRetainsLatestPendingValue()
    {
        using var directory = new TestDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new MutableProvider(Snapshot(clock.GetUtcNow(), 20));
        using var cache = new UsageSnapshotCache(provider, timeProvider: clock);
        var store = new UsageHistoryStore(directory.Path, timeProvider: clock);
        using var controller = new UsageHistoryController(store, [cache], clock);
        await cache.RefreshAsync(force: true);

        provider.Snapshot = Snapshot(clock.GetUtcNow(), 21);
        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.RefreshAsync(force: true);
        Assert.Single(store.Read("codex"));

        clock.Advance(TimeSpan.FromMinutes(4));
        await cache.RefreshAsync(force: true);

        var entries = store.Read("codex");
        Assert.Equal(2, entries.Count);
        Assert.Equal(21, entries[^1].PrimaryWindow!.UsedPercent);
    }

    [Fact]
    public async Task RecordsFreshIdenticalSnapshotsAfterFiveMinutes()
    {
        using var directory = new TestDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new MutableProvider(Snapshot(clock.GetUtcNow(), 20));
        using var cache = new UsageSnapshotCache(provider, timeProvider: clock);
        var store = new UsageHistoryStore(directory.Path, timeProvider: clock);
        using var controller = new UsageHistoryController(store, [cache], clock);

        await cache.RefreshAsync(force: true);
        clock.Advance(TimeSpan.FromMinutes(5));
        await cache.RefreshAsync(force: true);

        var entries = store.Read("codex");
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0].PrimaryWindow!.UsedPercent, entries[1].PrimaryWindow!.UsedPercent);
        Assert.True(entries[1].Timestamp - entries[0].Timestamp >= TimeSpan.FromMinutes(5));
    }
    [Fact]
    public async Task RotatesSegmentWhenSourceOrAccountScopeChangesWithinTheSameSourceKind()
    {
        using var directory = new TestDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new MutableProvider(Snapshot(clock.GetUtcNow(), 20, "endpoint-a", "account-a"));
        using var cache = new UsageSnapshotCache(provider, timeProvider: clock);
        var store = new UsageHistoryStore(directory.Path, timeProvider: clock);
        using var controller = new UsageHistoryController(store, [cache], clock);
        await cache.RefreshAsync(force: true);
        var firstSegment = Assert.Single(store.Read("codex")).SegmentId;

        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Snapshot = Snapshot(clock.GetUtcNow(), 21, "endpoint-a", "account-b");
        await cache.RefreshAsync(force: true);
        var secondSegment = store.Read("codex")[^1].SegmentId;

        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Snapshot = Snapshot(clock.GetUtcNow(), 22, "endpoint-b", "account-b");
        await cache.RefreshAsync(force: true);
        var thirdSegment = store.Read("codex")[^1].SegmentId;

        Assert.NotEqual(firstSegment, secondSegment);
        Assert.NotEqual(secondSegment, thirdSegment);
        Assert.NotEqual(firstSegment, thirdSegment);
    }

    [Fact]
    public async Task DisposeStopsSampling()
    {
        using var directory = new TestDirectory();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new MutableProvider(Snapshot(clock.GetUtcNow(), 20));
        using var cache = new UsageSnapshotCache(provider, timeProvider: clock);
        var store = new UsageHistoryStore(directory.Path, timeProvider: clock);
        var controller = new UsageHistoryController(store, [cache], clock);
        controller.Dispose();

        await cache.RefreshAsync(force: true);

        Assert.Empty(store.Read("codex"));
    }
    private static UsageSnapshot Snapshot(DateTimeOffset now, double used, string? source = "https://api.example", string? accountScope = null)
        => new("codex", "Codex", new UsageWindow(used, now.AddHours(4), 18000)
        {
            StartsAt = now.AddHours(-1),
            IsFixedWindow = true,
        }, null, null, false)
        {
            Source = source,
            AccountScope = accountScope,
            Metrics = [new UsageMetric("Tokens", used.ToString(System.Globalization.CultureInfo.InvariantCulture), "tokens", NumericValue: (decimal)used, SemanticKey: "tokens5h")],
        };

    private sealed class MutableProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        private TaskCompletionSource? _release;
        private TaskCompletionSource? _activeRelease;

        public UsageSnapshot Snapshot { get; set; } = snapshot;
        public TaskCompletionSource RefreshStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public UsageProviderDescriptor Descriptor { get; } = new("codex", "Codex");

        public void BlockNextRefresh()
        {
            RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseRefresh() => _activeRelease?.TrySetResult();

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            var release = _release;
            if (release is not null)
            {
                _release = null;
                _activeRelease = release;
                RefreshStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                _activeRelease = null;
            }

            return Snapshot;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokens-history-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
