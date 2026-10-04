using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Localization;
using Xunit.Abstractions;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class UsageOverviewMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RefreshingOneProviderPreservesOtherRowsDetailsAndCommands()
    {
        var firstProvider = new ControlledProvider("first");
        using var firstCache = new UsageSnapshotCache(firstProvider);
        using var secondCache = new UsageSnapshotCache(new ControlledProvider("second"));
        await firstCache.RefreshAsync(force: true);
        await secondCache.RefreshAsync(force: true);
        using var firstPage = new TokensLimitsPage(firstCache);
        using var secondPage = new TokensLimitsPage(secondCache);
        using var overview = new UsageOverviewPage([firstCache, secondCache], [firstPage, secondPage]);
        var rows = overview.GetItems();
        var secondDetails = rows[1].Details;
        var secondCommands = rows[1].MoreCommands;
        var changes = 0;
        overview.ItemsChanged += (_, _) => changes++;

        firstProvider.BlockNextRequest();
        var refresh = firstCache.RefreshAsync(force: true);
        await firstProvider.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(rows, overview.GetItems());
        Assert.Same(secondDetails, rows[1].Details);
        Assert.Same(secondCommands, rows[1].MoreCommands);
        Assert.Contains("Refreshing", rows[0].Subtitle, StringComparison.Ordinal);
        Assert.Equal(1, changes);

        firstProvider.Complete(75);
        await refresh;

        Assert.Same(secondDetails, rows[1].Details);
        Assert.Same(secondCommands, rows[1].MoreCommands);
        Assert.Contains("25%", rows[0].Subtitle, StringComparison.Ordinal);
        Assert.Same(firstPage, rows[0].Command);
        Assert.Same(secondPage, rows[1].Command);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ChangedRowUpdatesDetailsAndDiagnosticsWithoutReplacingCommands()
    {
        var provider = new ControlledProvider("first");
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var page = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [page]);
        var row = Assert.Single(overview.GetItems());
        var details = Assert.IsType<Details>(row.Details);
        var commands = row.MoreCommands;
        var diagnostics = Assert.IsType<CopyTextCommand>(Assert.IsType<CommandContextItem>(commands![^1]).Command);

        provider.BlockNextRequest();
        var refresh = cache.RefreshAsync(force: true);
        await provider.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Fetching the latest", Assert.IsType<Details>(row.Details).Body, StringComparison.Ordinal);
        provider.Fail(new HttpRequestException("test network failure"));
        await refresh;

        Assert.Same(details, row.Details);
        Assert.Same(commands, row.MoreCommands);
        Assert.Contains("Stale", details.Body, StringComparison.Ordinal);
        Assert.Contains("Network error", details.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Fetching the latest", details.Body, StringComparison.Ordinal);
        Assert.Contains("error=Network", diagnostics.Text, StringComparison.Ordinal);
        Assert.Contains("stale=True", diagnostics.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedCacheEventsKeepRowsAndSuppressHostNotifications()
    {
        using var cache = new UsageSnapshotCache(new ControlledProvider("first"));
        await cache.RefreshAsync(force: true);
        using var page = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [page]);
        cache.Clear();
        var rows = overview.GetItems();
        var details = rows[0].Details;
        var commands = rows[0].MoreCommands;
        var changes = 0;
        overview.ItemsChanged += (_, _) => changes++;

        cache.Clear();
        cache.Clear();

        Assert.Equal(0, changes);
        Assert.Same(rows, overview.GetItems());
        Assert.Same(details, rows[0].Details);
        Assert.Same(commands, rows[0].MoreCommands);
    }

    [Fact]
    public void UnchangedCacheEventsAvoidAllocatingDetailsAndActionGraphs()
    {
        using var cache = new UsageSnapshotCache(new ControlledProvider("first"));
        using var page = new TokensLimitsPage(cache);
        page.SetActive(false);
        using var overview = new UsageOverviewPage([cache], [page]);
        for (var iteration = 0; iteration < 10; iteration++) cache.Clear();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var iteration = 0; iteration < 100; iteration++) cache.Clear();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"100 unchanged cache events allocated {allocated} bytes.");
        // Includes the cache's state/cancellation objects, with room for runtime variation.
        Assert.True(allocated < 100 * 1024, $"Unchanged cache events allocated {allocated} bytes.");
    }

    [Fact]
    public async Task LanguageChangeUpdatesExistingDetailsAndActionsWithoutFetching()
    {
        var localization = new JsonLocalizationService(Path.Combine(AppContext.BaseDirectory, "lang"),
            Path.Combine(Path.GetTempPath(), $"TokensLimitsExtension.Memory.{Guid.NewGuid():N}"), "en");
        var provider = new ControlledProvider("first");
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync(force: true);
        using var page = new TokensLimitsPage(cache, localization: localization);
        using var overview = new UsageOverviewPage([cache], [page], localization: localization);
        var rows = overview.GetItems();
        var details = Assert.IsType<Details>(rows[0].Details);
        var commands = rows[0].MoreCommands;
        var refresh = Assert.IsType<CommandContextItem>(commands![0]);
        var changed = 0;
        overview.ItemsChanged += (_, _) => changed++;

        localization.ApplyPreference("ru");

        Assert.Equal("Обновить", refresh.Title);
        Assert.Contains("Доступно", details.Body, StringComparison.Ordinal);
        Assert.Same(rows, overview.GetItems());
        Assert.Same(details, rows[0].Details);
        Assert.Same(commands, rows[0].MoreCommands);
        Assert.Equal(1, changed);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ProviderReplacementUsesNewPageAndIgnoresRemovedCacheEvents()
    {
        using var firstCache = new UsageSnapshotCache(new ControlledProvider("first"));
        using var secondCache = new UsageSnapshotCache(new ControlledProvider("second"));
        await firstCache.RefreshAsync(force: true);
        await secondCache.RefreshAsync(force: true);
        using var firstPage = new TokensLimitsPage(firstCache);
        using var secondPage = new TokensLimitsPage(secondCache);
        using var replacementPage = new TokensLimitsPage(secondCache);
        using var overview = new UsageOverviewPage([firstCache, secondCache], [firstPage, secondPage]);

        overview.UpdateProviders([secondCache], [replacementPage]);
        var row = Assert.Single(overview.GetItems());
        var commands = row.MoreCommands;
        var changed = 0;
        overview.ItemsChanged += (_, _) => changed++;
        firstCache.Clear();

        Assert.Same(replacementPage, row.Command);
        Assert.Same(row, Assert.Single(overview.GetItems()));
        Assert.Same(commands, row.MoreCommands);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task ReplacingCacheWithSameIdRebindsRefreshAction()
    {
        var previousProvider = new ControlledProvider("same");
        var replacementProvider = new ControlledProvider("same");
        using var previousCache = new UsageSnapshotCache(previousProvider);
        using var replacementCache = new UsageSnapshotCache(replacementProvider);
        await previousCache.RefreshAsync(force: true);
        await replacementCache.RefreshAsync(force: true);
        using var page = new TokensLimitsPage(previousCache);
        using var overview = new UsageOverviewPage([previousCache], [page]);

        overview.UpdateProviders([replacementCache], [page]);
        replacementProvider.BlockNextRequest();
        var row = Assert.Single(overview.GetItems());
        var refresh = Assert.IsType<AnonymousCommand>(Assert.IsType<CommandContextItem>(row.MoreCommands![0]).Command);
        refresh.Invoke();
        await replacementProvider.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        replacementProvider.Complete(50);
        await replacementCache.RefreshAsync();

        Assert.Equal(1, previousProvider.CallCount);
        Assert.Equal(2, replacementProvider.CallCount);
        Assert.Contains("50%", row.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimultaneousCacheCallbacksDoNotReadEachOthersLockedState()
    {
        using var firstCache = new UsageSnapshotCache(new ControlledProvider("first"));
        using var secondCache = new UsageSnapshotCache(new ControlledProvider("second"));
        await firstCache.RefreshAsync(force: true);
        await secondCache.RefreshAsync(force: true);
        using var barrier = new Barrier(2);
        firstCache.StateChanged += (_, _) => Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
        secondCache.StateChanged += (_, _) => Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
        using var firstPage = new TokensLimitsPage(firstCache);
        using var secondPage = new TokensLimitsPage(secondCache);
        using var overview = new UsageOverviewPage([firstCache, secondCache], [firstPage, secondPage]);

        await Task.WhenAll(Task.Run(firstCache.Clear), Task.Run(secondCache.Clear)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(overview.GetItems(), row => Assert.Equal("данные недоступны", row.Subtitle));
    }

    private sealed class ControlledProvider(string id) : IUsageProvider
    {
        private TaskCompletionSource<UsageSnapshot>? _request;
        private int _callCount;
        public UsageProviderDescriptor Descriptor { get; } = new(id, id, dashboardUrl: "https://example.test/dashboard");
        public TaskCompletionSource RequestStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            RequestStarted.TrySetResult();
            return _request?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(CreateSnapshot(25));
        }

        public void BlockNextRequest()
        {
            RequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Complete(double usedPercent) => _request!.TrySetResult(CreateSnapshot(usedPercent));
        public void Fail(Exception exception) => _request!.TrySetException(exception);

        private UsageSnapshot CreateSnapshot(double usedPercent)
            => new(id, id, new UsageWindow(usedPercent, DateTimeOffset.UtcNow.AddHours(4), 18000), null, "pro", false);
    }
}
