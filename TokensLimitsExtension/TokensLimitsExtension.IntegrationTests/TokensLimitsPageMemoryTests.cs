using System.Globalization;
using System.Reflection;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsPageMemoryTests
{
    [Fact]
    public void UnopenedPagesDoNotRetainItemGraphsAfterCacheAndLanguageChanges()
    {
        var source = new StateSource();
        var localization = new MutableLocalization();
        using var page = new TokensLimitsPage(source, localization: localization);
        using var dockPage = new TokensLimitsPage(source, idSuffix: "dock", localization: localization);

        for (var index = 0; index < 10; index++) source.Publish(CreateSnapshot($"plan-{index}"));
        localization.UseRussian();

        Assert.Empty(RetainedItems(page));
        Assert.Empty(RetainedItems(dockPage));
        var itemsChanged = 0;
        page.ItemsChanged += (_, _) => itemsChanged++;
        var items = page.GetItems();
        Assert.Contains(items, item => item.Title == "План" && item.Subtitle == "plan-9");
        Assert.Same(items, page.GetItems());
        Assert.Empty(RetainedItems(dockPage));
        Assert.Equal(0, source.RequestCount);
        Assert.Equal(0, itemsChanged);
        Assert.Equal("com.tokenslimits.provider.memory.limits", page.Id);
        Assert.Equal("com.tokenslimits.provider.memory.limits.dock", dockPage.Id);
    }

    [Fact]
    public void MaterializedPagesKeepLiveUpdatesAndDistinctRowDetails()
    {
        var source = new StateSource();
        using var page = new TokensLimitsPage(source);
        using var dockPage = new TokensLimitsPage(source, idSuffix: "dock");
        source.Publish(CreateSnapshot("first"));
        var initial = page.GetItems();
        var changes = 0;
        page.ItemsChanged += (_, _) =>
        {
            changes++;
            Assert.Contains(page.GetItems(), item => item.Subtitle == "second");
        };

        source.Publish(CreateSnapshot("second"));

        Assert.Equal(1, changes);
        Assert.All(initial.Zip(page.GetItems()), pair => Assert.Same(pair.First, pair.Second));
        Assert.Empty(RetainedItems(dockPage));
        var normalRows = page.GetItems().OfType<ListItem>().Where(item => item.Details is not null).ToArray();
        var dockRows = dockPage.GetItems().OfType<ListItem>().Where(item => item.Details is not null).ToArray();
        Assert.Equal(normalRows.Length, dockRows.Length);
        Assert.NotSame(normalRows[0], dockRows[0]);
        Assert.NotSame(normalRows[0].Details, dockRows[0].Details);
        Assert.Equal(normalRows[0].Details!.Body, dockRows[0].Details!.Body);
        Assert.Equal(normalRows.Length, normalRows.Select(item => item.Details!.Body).Distinct().Count());
        Assert.All(normalRows, item =>
        {
            Assert.StartsWith($"**{item.Title}**", item.Details!.Body, StringComparison.Ordinal);
            Assert.Contains(item.Subtitle, item.Details.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DeactivationAndDisposalReleaseItemsAndReactivationUsesCurrentState()
    {
        var source = new StateSource();
        var localization = new MutableLocalization();
        using var page = new TokensLimitsPage(source, localization: localization);
        source.Publish(CreateSnapshot("first"));
        Assert.NotEmpty(page.GetItems());
        page.SetActive(false);
        Assert.Empty(page.GetItems());
        Assert.Empty(RetainedItems(page));
        Assert.Equal(0, source.SubscriberCount);
        source.Publish(CreateSnapshot("second"));
        localization.UseRussian();

        page.SetActive(true);

        Assert.Empty(RetainedItems(page));
        Assert.Equal(1, source.SubscriberCount);
        Assert.Contains(page.GetItems(), item => item.Title == "План" && item.Subtitle == "second");
        page.Dispose();
        page.Dispose();
        Assert.Empty(RetainedItems(page));
        Assert.Empty(page.GetItems());
        Assert.Equal(0, source.SubscriberCount);
        Assert.Equal(0, source.RequestCount);
    }

    [Fact]
    public void DetailsPreserveEscapingAndSafeCommonMetadataForEachRow()
    {
        var source = new StateSource();
        using var page = new TokensLimitsPage(source);
        source.Publish(CreateSnapshot("pro_*[x]<tag>\\`|\r\n\u0001") with
        {
            Source = "https://user:secret@provider.example/usage?token=secret",
            Metrics = [new UsageMetric("Metric(x)", "value_*")],
        });

        var rows = page.GetItems().OfType<ListItem>().Where(item => item.Details is not null).ToArray();

        Assert.All(rows, item =>
        {
            Assert.Contains("pro\\_\\*\\[x\\]&lt;tag&gt;\\\\\\`\\|  ", item.Details!.Body, StringComparison.Ordinal);
            Assert.Contains("https://provider.example/usage", item.Details.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", item.Details.Body, StringComparison.Ordinal);
            Assert.DoesNotContain('\u0001', item.Details.Body);
        });
        var metric = Assert.Single(rows, item => item.Title == "Metric(x)");
        Assert.StartsWith("**Metric\\(x\\)**", metric.Details!.Body, StringComparison.Ordinal);
        Assert.Contains("value\\_\\*", metric.Details.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentFirstReadAndCacheCallbacksDoNotDeadlock()
    {
        var source = new StateSource();
        using var page = new TokensLimitsPage(source);
        source.Publish(CreateSnapshot("initial"));
        page.ItemsChanged += (_, _) => Assert.NotEmpty(page.GetItems());
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 2_000; index++) Assert.NotEmpty(page.GetItems());
        });
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 100; index++) source.Publish(CreateSnapshot($"plan-{index}"));
        });

        await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(page.GetItems(), item => item.Subtitle == "plan-99");
        Assert.Equal(0, source.RequestCount);
    }

    [Fact]
    public void MaterializedPageUpdatesLocalizedRowsImmediately()
    {
        var source = new StateSource();
        var localization = new MutableLocalization();
        using var page = new TokensLimitsPage(source, localization: localization);
        source.Publish(CreateSnapshot("pro"));
        var plan = Assert.Single(page.GetItems(), item => item.Title == "Plan");
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;

        localization.UseRussian();

        Assert.Same(plan, Assert.Single(page.GetItems(), item => item.Title == "План"));
        Assert.Equal(1, changes);
        Assert.Equal(0, source.RequestCount);
    }

    private static UsageSnapshot CreateSnapshot(string plan)
        => new("memory", "Memory", null, null, plan, false)
        {
            FetchedAt = DateTimeOffset.Parse("2026-10-05T10:00:00Z", CultureInfo.InvariantCulture),
            Metrics = [new UsageMetric("Credits", "42")],
        };

    private static IListItem[] RetainedItems(TokensLimitsPage page)
        => Assert.IsType<IListItem[]>(typeof(TokensLimitsPage)
            .GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));

    private sealed class StateSource : IUsageProviderStateSource
    {
        private readonly object _stateGate = new();
        private UsageProviderState _state = new(null, null, null, false);
        public UsageProviderDescriptor Descriptor { get; } = new("memory", "Memory");
        public UsageProviderState State { get { lock (_stateGate) return _state; } }
        public event EventHandler? StateChanged;
        public int RequestCount { get; private set; }
        public int SubscriberCount => StateChanged?.GetInvocationList().Length ?? 0;

        public void Publish(UsageSnapshot snapshot)
        {
            // Match the cache contract: callbacks execute while its state gate is held.
            lock (_stateGate)
            {
                _state = new(snapshot, snapshot.FetchedAt, snapshot.FetchedAt, false);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.CompletedTask;
        }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(State.Snapshot!);
        }

        public bool TryGetSnapshot(out UsageSnapshot snapshot)
        {
            snapshot = State.Snapshot!;
            return snapshot is not null;
        }

        public void Invalidate() { }
    }

    private sealed class MutableLocalization : ILocalizationService
    {
        private bool _russian;
        public event EventHandler? LanguageChanged;
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public string GetString(string key, string? fallback = null)
            => key == "details.plan" ? _russian ? "План" : "Plan" : InvariantLocalizationService.Instance.GetString(key, fallback);
        public string Format(string key, params object?[] arguments)
            => string.Format(Culture, GetString(key), arguments);
        public void UseRussian()
        {
            _russian = true;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
