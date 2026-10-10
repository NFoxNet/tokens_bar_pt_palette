using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class UsageHistoryPageTests
{
    [Fact]
    public async Task PageShowsRecentSafeSamplesAndExplicitClearAction()
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        var items = fixture.Page.GetItems();
        Assert.Contains(items, item => item.Title == "Clear history" && item.Command is AnonymousCommand);
        var latestSample = Assert.IsType<ListItem>(Assert.Single(items, item => item.Title == "Codex · 31%"));
        Assert.Contains("Series ", latestSample.Details?.Body, StringComparison.Ordinal);
        Assert.Contains("tokens5h: 1200", latestSample.Details?.Body, StringComparison.Ordinal);
        var pace = Pace(items);
        Assert.Contains("no exhaustion is expected before reset", pace.Subtitle, StringComparison.Ordinal);
        Assert.Contains("Usage now: 31%", pace.Details?.Body, StringComparison.Ordinal);
        Assert.Contains("even pace by now:", pace.Details?.Body, StringComparison.Ordinal);

        Assert.IsType<AnonymousCommand>(items[0].Command).Invoke();
        Assert.Empty(fixture.Store.Read("codex"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("account-a", false)]
    [InlineData(null, true)]
    [InlineData("account-a", true)]
    public async Task RestartRetainsOldRowsWithoutTreatingThemAsCurrentPace(string? account, bool observeSnapshot)
    {
        using var fixture = new HistoryFixture();
        fixture.Provider.Snapshot = fixture.Provider.Snapshot with { AccountScope = account };
        await fixture.RecordSeries();
        AssertForecast(fixture.Page);
        using var emptyCache = new UsageSnapshotCache(new MutableProvider(fixture.Provider.Snapshot), timeProvider: fixture.Clock);
        using var restartedController = new UsageHistoryController(fixture.Store, [emptyCache], fixture.Clock);
        using var restartedPage = new UsageHistoryPage(restartedController, [emptyCache], timeProvider: fixture.Clock);
        if (observeSnapshot)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await emptyCache.RefreshAsync(force: true);
        }

        AssertInsufficient(restartedPage);
        Assert.Equal(3, SavedRows(restartedPage));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("unknown-account")]
    [InlineData("source")]
    [InlineData("reset")]
    [InlineData("configuration")]
    [InlineData("clear-cache")]
    [InlineData("window")]
    [InlineData("rolling")]
    [InlineData("estimate")]
    [InlineData("failure")]
    [InlineData("usage-reset")]
    public async Task CurrentStateChangeWithinThrottleWithdrawsPaceAndRetainsRows(string change)
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        AssertForecast(fixture.Page);
        var historyBefore = File.ReadAllBytes(fixture.HistoryPath);
        var changed = 0;
        fixture.Page.ItemsChanged += (_, _) => changed++;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var snapshot = fixture.Provider.Snapshot;
        switch (change)
        {
            case "account": fixture.Provider.Snapshot = snapshot with { AccountScope = "account-b" }; break;
            case "unknown-account": fixture.Provider.Snapshot = snapshot with { AccountScope = null }; break;
            case "source": fixture.Provider.Snapshot = snapshot with { Source = "endpoint-b" }; break;
            case "reset": fixture.Controller.ResetProvider("codex"); break;
            case "configuration": fixture.Controller.ResetProvider("codex"); fixture.Cache.Clear(); break;
            case "clear-cache": fixture.Cache.Clear(); break;
            case "window": fixture.Provider.Snapshot = snapshot with { PrimaryWindow = snapshot.PrimaryWindow! with { ResetAt = snapshot.PrimaryWindow.ResetAt.AddHours(1) } }; break;
            case "rolling": fixture.Provider.Snapshot = snapshot with { PrimaryWindow = snapshot.PrimaryWindow! with { IsFixedWindow = false } }; break;
            case "estimate": fixture.Provider.Snapshot = snapshot with { IsEstimate = true }; break;
            case "failure": fixture.Provider.Fail = true; break;
            case "usage-reset": fixture.Provider.Snapshot = snapshot with { PrimaryWindow = snapshot.PrimaryWindow! with { UsedPercent = 1 } }; break;
        }
        if (change is not ("reset" or "configuration" or "clear-cache")) await fixture.Cache.RefreshAsync(force: true);

        AssertInsufficient(fixture.Page);
        Assert.True(changed > 0);
        Assert.Equal(3, SavedRows(fixture.Page));
        Assert.Equal(historyBefore, File.ReadAllBytes(fixture.HistoryPath));
    }

    [Fact]
    public async Task PaceReturnsOnlyAfterThreeCurrentSegmentSamplesSpanningFifteenMinutes()
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Provider.Snapshot = fixture.Provider.Snapshot with { AccountScope = "account-b" };
        await fixture.Cache.RefreshAsync(force: true);
        AssertInsufficient(fixture.Page);
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        await fixture.Record(32);
        AssertInsufficient(fixture.Page);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Record(33);
        AssertInsufficient(fixture.Page);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.Record(34);
        AssertForecast(fixture.Page);
        Assert.Equal(6, SavedRows(fixture.Page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetItemsRechecksFreshnessAndWindowExpiryWithoutAppend(bool windowExpiry)
    {
        using var fixture = new HistoryFixture();
        if (windowExpiry)
            fixture.Provider.Snapshot = fixture.Provider.Snapshot with { PrimaryWindow = fixture.Provider.Snapshot.PrimaryWindow! with { ResetAt = fixture.Clock.GetUtcNow().AddMinutes(25) } };
        await fixture.RecordSeries();
        AssertForecast(fixture.Page);
        fixture.Clock.Advance(TimeSpan.FromMinutes(windowExpiry ? 6 : 16));

        AssertInsufficient(fixture.Page);
        Assert.Equal(3, SavedRows(fixture.Page));
    }

    [Fact]
    public async Task RepeatedReadsProjectTimeWithoutProviderFetchHistoryReadOrWrite()
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        var previousSubtitle = Pace(fixture.Page.GetItems()).Subtitle;
        var previousBody = Pace(fixture.Page.GetItems()).Details!.Body;
        var calls = fixture.Provider.Calls;
        var content = File.ReadAllBytes(fixture.HistoryPath);
        var timestamp = File.GetLastWriteTimeUtc(fixture.HistoryPath);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        for (var index = 0; index < 10; index++) AssertForecast(fixture.Page);
        Assert.Equal(previousSubtitle, Pace(fixture.Page.GetItems()).Subtitle);
        Assert.NotEqual(previousBody, Pace(fixture.Page.GetItems()).Details!.Body);
        Assert.Equal(calls, fixture.Provider.Calls);
        Assert.Equal(content, File.ReadAllBytes(fixture.HistoryPath));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.HistoryPath));
        File.WriteAllText(fixture.HistoryPath, "{broken");
        AssertForecast(fixture.Page);
        Assert.DoesNotContain(fixture.Page.GetItems(), item => item.Title == "History storage error");
    }

    [Theory]
    [InlineData("same-series")]
    [InlineData("account")]
    [InlineData("source")]
    [InlineData("reset")]
    public async Task FailedWriteWithdrawsPaceEvenWithRetainedValidSeries(string change)
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        var historyBefore = File.ReadAllBytes(fixture.HistoryPath);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        if (change == "reset") fixture.Controller.ResetProvider("codex");
        fixture.Provider.Snapshot = fixture.Provider.Snapshot with
        {
            AccountScope = change == "account" ? "account-b" : fixture.Provider.Snapshot.AccountScope,
            Source = change == "source" ? "endpoint-b" : fixture.Provider.Snapshot.Source,
            Metrics = [new UsageMetric("Invalid", "NaN", "tokens", Used: double.NaN, SemanticKey: "tokens5h")],
        };
        await fixture.Cache.RefreshAsync(force: true);

        Assert.Equal(UsageHistoryErrorKind.InvalidData, fixture.Controller.LastErrorKind);
        AssertInsufficient(fixture.Page);
        Assert.Equal(historyBefore, File.ReadAllBytes(fixture.HistoryPath));
        Assert.Equal(3, SavedRows(fixture.Page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstimateOrCacheClearThenApiCannotReviveThePrecedingSeries(bool clear)
    {
        using var fixture = new HistoryFixture();
        await fixture.RecordSeries();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        if (clear) fixture.Cache.Clear();
        else
        {
            fixture.Provider.Snapshot = fixture.Provider.Snapshot with { IsEstimate = true };
            await fixture.Cache.RefreshAsync(force: true);
        }
        AssertInsufficient(fixture.Page);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Provider.Snapshot = fixture.Provider.Snapshot with { IsEstimate = false };
        await fixture.Cache.RefreshAsync(force: true);
        AssertInsufficient(fixture.Page);
        Assert.Equal(3, SavedRows(fixture.Page));
    }

    private static ListItem Pace(IListItem[] items)
        => Assert.IsType<ListItem>(Assert.Single(items, item => item.Title == "Usage pace · Codex"));
    private static void AssertForecast(UsageHistoryPage page)
        => Assert.StartsWith("Estimate · ", Pace(page.GetItems()).Subtitle, StringComparison.Ordinal);
    private static void AssertInsufficient(UsageHistoryPage page)
        => Assert.Equal("Not enough recent fixed-window history", Pace(page.GetItems()).Subtitle);
    private static int SavedRows(UsageHistoryPage page)
        => page.GetItems().Count(item => item.Title.StartsWith("Codex · ", StringComparison.Ordinal));

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokens-history-page-" + Guid.NewGuid().ToString("N"));
        public HistoryFixture()
        {
            Directory.CreateDirectory(_directory);
            Clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-20));
            Provider = new MutableProvider(new UsageSnapshot("codex", "Codex", new UsageWindow(28, Clock.GetUtcNow().AddHours(4), 18_000)
            {
                StartsAt = Clock.GetUtcNow().AddHours(-1), IsFixedWindow = true,
            }, null, null, false) { Source = "endpoint-a", AccountScope = "account-a" });
            Cache = new UsageSnapshotCache(Provider, timeProvider: Clock);
            Store = new UsageHistoryStore(_directory, timeProvider: Clock);
            Controller = new UsageHistoryController(Store, [Cache], Clock);
            Page = new UsageHistoryPage(Controller, [Cache], timeProvider: Clock);
        }
        public FakeTimeProvider Clock { get; }
        public MutableProvider Provider { get; }
        public UsageSnapshotCache Cache { get; }
        public UsageHistoryStore Store { get; }
        public UsageHistoryController Controller { get; }
        public UsageHistoryPage Page { get; }
        public string HistoryPath => Directory.GetFiles(_directory, "*.json").Single();
        public async Task RecordSeries()
        {
            await Record(28);
            Clock.Advance(TimeSpan.FromMinutes(10));
            await Record(30);
            Clock.Advance(TimeSpan.FromMinutes(10));
            await Record(31);
        }
        public async Task Record(double used)
        {
            Provider.Snapshot = Provider.Snapshot with
            {
                PrimaryWindow = Provider.Snapshot.PrimaryWindow! with { UsedPercent = used },
                Metrics = [new UsageMetric("Tokens", "1200", "tokens", NumericValue: 1200, SemanticKey: "tokens5h")],
            };
            await Cache.RefreshAsync(force: true);
        }
        public void Dispose()
        {
            Page.Dispose(); Controller.Dispose(); Cache.Dispose(); Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MutableProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageSnapshot Snapshot { get; set; } = snapshot;
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public UsageProviderDescriptor Descriptor { get; } = new("codex", "Codex");
        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Fail ? Task.FromException<UsageSnapshot>(new HttpRequestException("Temporary test failure")) : Task.FromResult(Snapshot);
        }
    }
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    [Fact]
    public void PageSurfacesCorruptHistoryOnFirstOpen()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokens-history-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var providerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("codex")));
            File.WriteAllText(System.IO.Path.Combine(directory, providerHash + ".json"), "{broken");
            using var cache = new UsageSnapshotCache(new StubProvider());
            using var controller = new UsageHistoryController(new UsageHistoryStore(directory), [cache]);
            using var page = new UsageHistoryPage(controller, [cache]);

            Assert.Contains(page.GetItems(), item => item.Title == "History storage error");
            Assert.Equal("{broken", File.ReadAllText(System.IO.Path.Combine(directory, providerHash + ".json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    private sealed class StubProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("codex", "Codex");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("History page must not fetch provider data.");
    }
}
