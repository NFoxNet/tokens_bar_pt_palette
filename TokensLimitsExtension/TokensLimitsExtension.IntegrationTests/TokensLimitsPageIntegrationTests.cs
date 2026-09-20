using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Collections;
using System.Reflection;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsPageIntegrationTests
{
    [Fact]
    public void GetItemsDoesNotStartRefreshOrRaiseItemsChanged()
    {
        var provider = new BlockingGenericProvider();
        using var cache = new UsageSnapshotCache(provider);
        using var detailsPage = new TokensLimitsPage(cache);
        using var overviewPage = new UsageOverviewPage([cache], [detailsPage]);
        var detailsItemsChanged = 0;
        var overviewItemsChanged = 0;
        detailsPage.ItemsChanged += (_, _) => detailsItemsChanged++;
        overviewPage.ItemsChanged += (_, _) => overviewItemsChanged++;

        for (var index = 0; index < 1_000; index++)
        {
            _ = detailsPage.GetItems();
            _ = overviewPage.GetItems();
        }

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, detailsItemsChanged);
        Assert.Equal(0, overviewItemsChanged);
    }

    [Fact]
    public async Task CommandsProviderStartsInitialRefreshWithoutReadingPageItems()
    {
        using var testDirectory = new TestDirectory();
        var usageProvider = new StartingGenericProvider();
        using var registry = new UsageProviderRegistry([usageProvider]);
        using var commandsProvider = new TokensLimitsExtensionCommandsProvider(
            null,
            registry,
            new global::TokensLimitsExtension.Settings.TokensLimitsSettings(testDirectory.Path));

        await usageProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, usageProvider.CallCount);
    }

    [Fact]
    public async Task PageReturnsDetailedLimitItemsWithExpectedTitles()
    {
        var now = DateTimeOffset.UtcNow;
        using var page = new TokensLimitsPage(new FakeUsageProvider(
            new CodexUsageSnapshot(38, now.AddHours(1), 12, now.AddDays(2), "pro", false)));

        await page.RefreshAsync();
        var items = page.GetItems();

        Assert.Equal(6, items.Length);
        Assert.Contains(items, item => item.Title == "5ч");
        Assert.Contains(items, item => item.Title == "Еженедельно");
        Assert.Contains(items, item => item.Subtitle.Contains("62% осталось", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Subtitle.Contains("через", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Title == "План" && item.Subtitle == "pro");
        Assert.Contains(items, item => item.Title == "Refresh" && item.Command is AnonymousCommand);
        var diagnostics = Assert.Single(items, item => item.Title == "Copy safe diagnostics");
        Assert.IsType<CopyTextCommand>(diagnostics.Command);
        Assert.DoesNotContain("test-token", ((CopyTextCommand)diagnostics.Command!).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableDetailsExplainHowToFixMissingConfiguration()
    {
        using var cache = new UsageSnapshotCache(new MissingConfigurationProvider());
        using var page = new TokensLimitsPage(cache);

        await page.RefreshAsync();

        var status = Assert.Single(page.GetItems(), item => item.Title == "Missing configuration");
        Assert.Contains("settings", status.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetailsExposeOnlyHttpsDashboardActions()
    {
        var snapshot = new UsageSnapshot(
            "dashboard",
            "Dashboard",
            new UsageWindow(10, DateTimeOffset.UtcNow.AddHours(1), 3600),
            null,
            null,
            false);
        using var securePage = new TokensLimitsPage(new FakeGenericProvider(
            "dashboard",
            "Dashboard",
            snapshot,
            "https://provider.example/usage"));
        using var insecurePage = new TokensLimitsPage(new FakeGenericProvider(
            "dashboard-http",
            "Dashboard HTTP",
            snapshot with { ProviderId = "dashboard-http" },
            "http://provider.example/usage"));

        await securePage.RefreshAsync();
        await insecurePage.RefreshAsync();

        Assert.Single(securePage.GetItems(), item => item.Command is OpenUrlCommand);
        Assert.DoesNotContain(insecurePage.GetItems(), item => item.Command is OpenUrlCommand);
    }

    [Fact]
    public async Task StaleDetailsShowSafeSourceAndLastSuccessfulRefresh()
    {
        var provider = new FlakyGenericProvider(new UsageSnapshot(
            "stale-source",
            "Stale source",
            new UsageWindow(10, DateTimeOffset.UtcNow.AddHours(1), 3600),
            null,
            null,
            false)
        {
            Source = "https://provider.example/usage?access_token=test-token",
        });
        using var cache = new UsageSnapshotCache(provider);
        using var page = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [page]);

        await page.RefreshAsync();
        using var dock = new UsageDockBandItem(cache);
        provider.Fail = true;
        await cache.RefreshAsync(force: true);

        var items = page.GetItems();
        Assert.Contains(items, item => item.Title == "Source" && item.Subtitle == "https://provider.example/usage");
        Assert.Contains(items, item => item.Title == "Last successful refresh");
        Assert.DoesNotContain(items, item => item.Subtitle.Contains("test-token", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Title == "Stale");
        Assert.Contains("Offline", dock.DockSubtitle, StringComparison.Ordinal);
        Assert.Contains(overview.GetItems(), item => item.Subtitle.Contains("Stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IdenticalRefreshStateKeepsDetailsItemsStable()
    {
        var snapshot = new UsageSnapshot(
            "stable",
            "Stable",
            new UsageWindow(25, DateTimeOffset.UtcNow.AddDays(2), 3600),
            null,
            "pro",
            false);
        using var cache = new UsageSnapshotCache(new MutableGenericProvider("stable", "Stable", snapshot));
        using var page = new TokensLimitsPage(cache);

        await page.RefreshAsync();
        var firstItems = page.GetItems();
        var itemsChanged = 0;
        page.ItemsChanged += (_, _) => itemsChanged++;

        await cache.RefreshAsync(force: true);

        var secondItems = page.GetItems();
        Assert.Equal(0, itemsChanged);
        Assert.Equal(firstItems.Length, secondItems.Length);
        Assert.All(firstItems.Zip(secondItems), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public async Task ChangedValuesReuseDetailsItemsWhenCompositionIsStable()
    {
        var provider = new MutableGenericProvider(
            "stable",
            "Stable",
            new UsageSnapshot(
                "stable",
                "Stable",
                new UsageWindow(25, DateTimeOffset.UtcNow.AddDays(2), 3600),
                null,
                "pro",
                false));
        using var cache = new UsageSnapshotCache(provider);
        using var page = new TokensLimitsPage(cache);

        await page.RefreshAsync();
        var firstItems = page.GetItems();
        provider.SetSnapshot(new UsageSnapshot(
            "stable",
            "Stable",
            new UsageWindow(50, DateTimeOffset.UtcNow.AddDays(2), 3600),
            null,
            "pro",
            false));

        await cache.RefreshAsync(force: true);

        var secondItems = page.GetItems();
        Assert.Equal(firstItems.Length, secondItems.Length);
        Assert.All(firstItems.Zip(secondItems), pair => Assert.Same(pair.First, pair.Second));
        Assert.Contains("50% осталось", secondItems[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedValuesReuseOverviewItemsWhenProviderCompositionIsStable()
    {
        var provider = new MutableGenericProvider(
            "stable",
            "Stable",
            new UsageSnapshot(
                "stable",
                "Stable",
                new UsageWindow(25, DateTimeOffset.UtcNow.AddDays(2), 3600),
                null,
                "pro",
                false));
        using var cache = new UsageSnapshotCache(provider);
        using var details = new TokensLimitsPage(cache);
        using var overview = new UsageOverviewPage([cache], [details]);

        await overview.RefreshAsync();
        var firstItems = overview.GetItems();
        provider.SetSnapshot(new UsageSnapshot(
            "stable",
            "Stable",
            new UsageWindow(50, DateTimeOffset.UtcNow.AddDays(2), 3600),
            null,
            "pro",
            false));

        await cache.RefreshAsync(force: true);

        var secondItems = overview.GetItems();
        Assert.Single(secondItems);
        Assert.Same(firstItems[0], secondItems[0]);
        Assert.Contains("50%", secondItems[0].Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsProviderExposesExactlyOneCommand()
    {
        using var testDirectory = new TestDirectory();
        using var provider = new TokensLimitsExtensionCommandsProvider(
            new FakeUsageProvider(
                new CodexUsageSnapshot(1, DateTimeOffset.UtcNow, 2, DateTimeOffset.UtcNow, null, true)),
            null,
            new global::TokensLimitsExtension.Settings.TokensLimitsSettings(testDirectory.Path));

        Assert.Single(provider.TopLevelCommands());
        Assert.Equal("com.tokenslimits.extension", provider.Id);
    }

    [Fact]
    public async Task DockBandContainsEveryProviderFromTheRegistry()
    {
        var codexProvider = new FakeGenericProvider(
            "codex",
            "Codex",
            new UsageSnapshot(
                "codex",
                "Codex",
                new UsageWindow(10, DateTimeOffset.UtcNow.AddHours(1), 3600),
                new UsageWindow(20, DateTimeOffset.UtcNow.AddDays(1), 86400),
                null,
                false));
        var otherProvider = new FakeGenericProvider(
            "other-provider",
            "Other Provider",
            new UsageSnapshot(
                "other-provider",
                "Other Provider",
                new UsageWindow(30, DateTimeOffset.UtcNow.AddHours(1), 3600),
                new UsageWindow(40, DateTimeOffset.UtcNow.AddDays(1), 86400),
                null,
                false));
        using var testDirectory = new TestDirectory();
        using var registry = new UsageProviderRegistry([codexProvider, otherProvider]);
        using var provider = new TokensLimitsExtensionCommandsProvider(
            null,
            registry,
            new global::TokensLimitsExtension.Settings.TokensLimitsSettings(testDirectory.Path));

        var bands = provider.GetDockBands();

        var band = Assert.Single(bands);
        Assert.Equal(TokensLimitsDockBandPage.StableId, band.Command!.Id);
        Assert.Equal("Tokens Limits", band.Title);
        var dockPage = Assert.IsType<TokensLimitsDockBandPage>(band.Command);
        var dockItems = dockPage.GetItems();
        Assert.Equal(2, dockItems.Length);
        await Task.WhenAll(dockItems.Cast<UsageDockBandItem>().Select(item => item.RefreshAsync()));
        var overviewCommand = Assert.Single(provider.TopLevelCommands()).Command;
        var overviewPage = Assert.IsType<UsageOverviewPage>(overviewCommand);
        await overviewPage.RefreshAsync();
        var overviewProviderItems = overviewPage.GetItems();
        Assert.Equal(2, overviewProviderItems.Length);
        var dockItem = Assert.Single(dockItems.Cast<UsageDockBandItem>(), item => item.Title == "Other Provider");
        var overviewProviderItem = Assert.Single(overviewProviderItems, item => item.Title == "Other Provider");
        Assert.Contains("\\70%,", dockItem.Subtitle, StringComparison.Ordinal);
        Assert.EndsWith("\\60%", dockItem.Subtitle, StringComparison.Ordinal);
        Assert.Equal("com.tokenslimits.provider.other-provider.limits.dock", dockItem.Command!.Id);
        Assert.IsType<TokensLimitsPage>(dockItem.Command);
        Assert.NotSame(overviewProviderItem.Command, dockItem.Command);
        Assert.NotEqual(overviewProviderItem.Command!.Id, dockItem.Command.Id);
    }

    [Fact]
    public async Task GenericDockItemRendersProviderSnapshotAndCanBeDisposed()
    {
        var provider = new FakeGenericProvider(
            "codex-like",
            "Codex-like",
            new UsageSnapshot(
                "codex-like",
                "Codex-like",
                new UsageWindow(38, DateTimeOffset.UtcNow.AddHours(1), 18000),
                new UsageWindow(12, DateTimeOffset.UtcNow.AddDays(2), 604800),
                "pro",
                false));
        using var item = new UsageDockBandItem(provider);

        await item.RefreshAsync();

        Assert.Equal("Codex-like", item.Title);
        Assert.Equal("5ч\\62%, 7д\\88%", item.Subtitle);
        Assert.Equal(item.Subtitle, item.DockSubtitle);
        item.Dispose();
        Assert.True(item.IsDisposed);
    }

    [Fact]
    public async Task DockBandInvalidatesItemsWhenSharedSnapshotChanges()
    {
        var provider = new MutableGenericProvider(
            "codex",
            "Codex",
            CreateSnapshot("codex", "Codex", primaryUsedPercent: 100));
        using var cache = new UsageSnapshotCache(provider);
        await cache.RefreshAsync();
        using var dockItem = new UsageDockBandItem(cache);
        using var dockPage = new TokensLimitsDockBandPage();
        dockPage.UpdateItems([dockItem]);

        var itemsChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dockPage.ItemsChanged += (_, _) => itemsChanged.TrySetResult();

        provider.SetSnapshot(CreateSnapshot("codex", "Codex", primaryUsedPercent: 4));
        await cache.RefreshAsync(force: true);

        await itemsChanged.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("96%", dockItem.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedProviderToggleReusesCachedSurfacesAndDisposeIsIdempotent()
    {
        using var testDirectory = new TestDirectory();
        using var settings = new global::TokensLimitsExtension.Settings.TokensLimitsSettings(testDirectory.Path);
        var codex = new FakeGenericProvider(
            "codex",
            "Codex",
            CreateSnapshot("codex", "Codex", primaryUsedPercent: 10));
        var amp = new AsyncGenericProvider(
            "amp",
            "Amp",
            CreateSnapshot("amp", "Amp", primaryUsedPercent: 20));
        using var registry = new UsageProviderRegistry([codex, amp]);
        using var provider = new TokensLimitsExtensionCommandsProvider(
            null,
            registry,
            settings,
            settingsDrivenProviders: true);

        var ampToggle = GetRegisteredSetting<ToggleSetting>(settings, "tokensLimits.providers.amp.enabled");
        ampToggle.Value = true;
        ApplySettingsChange(settings);
        var initialAmpPage = Assert.Single(
            GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
            page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase));
        var initialAmpDockPage = Assert.Single(
            GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"),
            page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase));
        var initialAmpDockItem = Assert.Single(
            GetPrivateSurfaceArray<UsageDockBandItem>(provider, "_dockBandItems"),
            item => item.Title == "Amp");
        var ampCache = Assert.Single(
            GetPrivateSurfaceArray<UsageSnapshotCache>(provider, "_snapshotCaches"),
            cache => cache.Descriptor.Id == "amp");
        await initialAmpPage.RefreshAsync();
        Assert.Contains(
            initialAmpDockPage.GetItems(),
            item => (item.Subtitle ?? string.Empty).Contains("80%", StringComparison.Ordinal));
        var callsBeforeDisable = amp.CallCount;
        ampToggle.Value = false;
        ApplySettingsChange(settings);
        Assert.DoesNotContain(
            GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
            page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase));
        Assert.False(initialAmpPage.IsActive);
        Assert.False(initialAmpDockPage.IsActive);
        Assert.False(initialAmpDockItem.IsActive);
        Assert.Empty(initialAmpPage.GetItems());
        Assert.Empty(initialAmpDockPage.GetItems());
        Assert.Equal(0, GetStateChangedSubscriberCount(ampCache));
        ampCache.Invalidate();
        await initialAmpPage.RefreshAsync();
        await initialAmpDockPage.RefreshAsync();
        await initialAmpDockItem.RefreshAsync();
        Assert.Equal(callsBeforeDisable, amp.CallCount);
        var initialPages = GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages");
        var codexPage = Assert.Single(initialPages, page => page.Id.Contains("codex", StringComparison.OrdinalIgnoreCase));

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var callsBeforeEnable = amp.CallCount;
            ampToggle.Value = true;
            ApplySettingsChange(settings);
            await initialAmpPage.RefreshAsync();
            Assert.Equal(callsBeforeEnable + 1, amp.CallCount);
            Assert.True(initialAmpPage.IsActive);
            Assert.True(initialAmpDockPage.IsActive);
            Assert.True(initialAmpDockItem.IsActive);
            Assert.Contains(
                initialAmpDockPage.GetItems(),
                item => (item.Subtitle ?? string.Empty).Contains("80%", StringComparison.Ordinal));
            Assert.Contains("80%", initialAmpDockItem.DockSubtitle, StringComparison.Ordinal);
            Assert.Same(initialAmpPage, Assert.Single(
                GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
                page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase)));
            Assert.Same(initialAmpDockPage, Assert.Single(
                GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"),
                page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase)));
            Assert.Same(initialAmpDockItem, Assert.Single(
                GetPrivateSurfaceArray<UsageDockBandItem>(provider, "_dockBandItems"),
                item => item.Title == "Amp"));
            var activePages = GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages");
            Assert.Collection(
                activePages,
                page => Assert.Equal("com.tokenslimits.codex.limits", page.Id),
                page => Assert.Equal("com.tokenslimits.provider.amp.limits", page.Id));
            Assert.Equal(2, GetPrivateDictionaryCount(provider, "_pagesByProviderId"));
            Assert.Equal(2, GetPrivateDictionaryCount(provider, "_dockPagesByProviderId"));
            Assert.Equal(2, GetPrivateDictionaryCount(provider, "_dockBandItemsByProviderId"));
            var callsBeforeNextDisable = amp.CallCount;
            ampToggle.Value = false;
            ApplySettingsChange(settings);
            ampCache.Invalidate();
            Assert.False(initialAmpPage.IsActive);
            Assert.False(initialAmpDockPage.IsActive);
            Assert.False(initialAmpDockItem.IsActive);
            Assert.Equal(0, GetStateChangedSubscriberCount(ampCache));
            await initialAmpPage.RefreshAsync();
            await initialAmpDockPage.RefreshAsync();
            await initialAmpDockItem.RefreshAsync();
            Assert.Equal(callsBeforeNextDisable, amp.CallCount);

            var currentPage = Assert.Single(
                GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
                page => page.Id.Contains("codex", StringComparison.OrdinalIgnoreCase));
            Assert.Same(codexPage, currentPage);
        }

        var dockPage = Assert.Single(GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"));
        Assert.NotSame(codexPage, dockPage);
        provider.Dispose();
        provider.Dispose();
        RebuildEnabledSurfaces(provider);
        Assert.Empty(GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"));
        Assert.Empty(GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"));
        Assert.Empty(GetPrivateSurfaceArray<UsageDockBandItem>(provider, "_dockBandItems"));
        Assert.Empty(initialAmpPage.GetItems());
        Assert.Empty(initialAmpDockPage.GetItems());
        Assert.True(initialAmpDockItem.IsDisposed);
        Assert.Equal(0, GetStateChangedSubscriberCount(ampCache));
    }

    [Fact]
    public void ReentrantSettingsChangePublishesLatestSurfaceComposition()
    {
        using var testDirectory = new TestDirectory();
        using var settings = new global::TokensLimitsExtension.Settings.TokensLimitsSettings(testDirectory.Path);
        var codex = new FakeGenericProvider(
            "codex",
            "Codex",
            CreateSnapshot("codex", "Codex", primaryUsedPercent: 10));
        var amp = new FakeGenericProvider(
            "amp",
            "Amp",
            CreateSnapshot("amp", "Amp", primaryUsedPercent: 20));
        using var registry = new UsageProviderRegistry([codex, amp]);
        using var provider = new TokensLimitsExtensionCommandsProvider(
            null,
            registry,
            settings,
            settingsDrivenProviders: true);

        var ampToggle = GetRegisteredSetting<ToggleSetting>(settings, "tokensLimits.providers.amp.enabled");
        var overview = GetPrivateField<UsageOverviewPage>(provider, "_overviewPage");
        var dockBandPage = GetPrivateField<TokensLimitsDockBandPage>(provider, "_dockBandPage");
        var changedReentrantly = false;
        var ampCache = Assert.Single(
            GetPrivateSurfaceArray<UsageSnapshotCache>(provider, "_snapshotCaches"),
            cache => cache.Descriptor.Id == "amp");
        var refreshCoordinator = GetPrivateField<UsageRefreshCoordinator>(provider, "_refreshCoordinator");
        TokensLimitsPage? retainedAmpPage = null;
        TokensLimitsPage? retainedAmpDockPage = null;
        UsageDockBandItem? retainedAmpDockItem = null;
        Windows.Foundation.TypedEventHandler<object, IItemsChangedEventArgs>? overviewChanged = null;
        overviewChanged = (_, _) =>
        {
            if (changedReentrantly)
            {
                return;
            }

            changedReentrantly = true;
            overview.ItemsChanged -= overviewChanged;
            retainedAmpPage = Assert.Single(
                GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
                page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase));
            retainedAmpDockPage = Assert.Single(
                GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"),
                page => page.Id.Contains("amp", StringComparison.OrdinalIgnoreCase));
            retainedAmpDockItem = Assert.Single(
                GetPrivateSurfaceArray<UsageDockBandItem>(provider, "_dockBandItems"),
                item => item.Title == "Amp");
            ampToggle.Value = false;
            ApplySettingsChange(settings);
            Assert.False(retainedAmpPage.IsActive);
            Assert.False(retainedAmpDockPage.IsActive);
            Assert.False(retainedAmpDockItem.IsActive);
            Assert.Empty(retainedAmpPage.GetItems());
            Assert.Empty(retainedAmpDockPage.GetItems());
            Assert.DoesNotContain("amp", GetPrivateField<string[]>(provider, "_coordinatorProviderIds"), StringComparer.OrdinalIgnoreCase);
            Assert.False(HasStateChangedSubscriber(ampCache, retainedAmpPage));
            Assert.False(HasStateChangedSubscriber(ampCache, retainedAmpDockPage));
            Assert.False(HasStateChangedSubscriber(ampCache, retainedAmpDockItem));
            ampCache.Invalidate();
            var callsAfterDisable = amp.CallCount;
            retainedAmpPage.RefreshAsync().GetAwaiter().GetResult();
            retainedAmpDockPage.RefreshAsync().GetAwaiter().GetResult();
            retainedAmpDockItem.RefreshAsync().GetAwaiter().GetResult();
            refreshCoordinator.RefreshAll();
            Assert.Equal(callsAfterDisable, amp.CallCount);
        };
        overview.ItemsChanged += overviewChanged;

        ampToggle.Value = true;
        ApplySettingsChange(settings);

        Assert.True(changedReentrantly);
        Assert.Collection(
            GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_pages"),
            page => Assert.Equal("com.tokenslimits.codex.limits", page.Id));
        Assert.Collection(
            GetPrivateSurfaceArray<TokensLimitsPage>(provider, "_dockPages"),
            page => Assert.Equal("com.tokenslimits.codex.limits.dock", page.Id));
        Assert.Collection(
            GetPrivateSurfaceArray<UsageDockBandItem>(provider, "_dockBandItems"),
            item => Assert.Equal("Codex", item.Title));
        Assert.Collection(
            dockBandPage.GetItems(),
            item => Assert.Equal("Codex", item.Title));
    }

    private sealed class FakeUsageProvider(CodexUsageSnapshot snapshot) : ICodexUsageProvider
    {
        public Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FakeGenericProvider(
        string id,
        string displayName,
        UsageSnapshot snapshot,
        string? dashboardUrl = null) : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName, dashboardUrl: dashboardUrl);

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class MutableGenericProvider(string id, string displayName, UsageSnapshot snapshot) : IUsageProvider
    {
        private UsageSnapshot _snapshot = snapshot;

        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Volatile.Read(ref _snapshot));

        public void SetSnapshot(UsageSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
    }

    private sealed class AsyncGenericProvider(
        string id,
        string displayName,
        UsageSnapshot snapshot) : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName);

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return snapshot;
        }
    }

    private sealed class BlockingGenericProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("blocking", "Blocking");

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class MissingConfigurationProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("missing-configuration", "Missing configuration");

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new UsageProviderConfigurationException("test-token is required");
    }

    private sealed class FlakyGenericProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new("stale-source", "Stale source");

        public bool Fail { get; set; }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Fail
                ? throw new UsageProviderRequestException("temporary failure", failureKind: UsageProviderFailureKind.Network)
                : Task.FromResult(snapshot);
    }

    private sealed class StartingGenericProvider : IUsageProvider
    {
        private int _callCount;

        public UsageProviderDescriptor Descriptor { get; } = new("starting", "Starting");

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private static UsageSnapshot CreateSnapshot(string id, string displayName, int primaryUsedPercent)
        => new(
            id,
            displayName,
            new UsageWindow(primaryUsedPercent, DateTimeOffset.UtcNow.AddHours(1), 3600),
            new UsageWindow(53, DateTimeOffset.UtcNow.AddDays(1), 86400),
            null,
            false);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"TokensLimitsExtension.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static void ApplySettingsChange(global::TokensLimitsExtension.Settings.TokensLimitsSettings settings)
    {
        var method = typeof(global::TokensLimitsExtension.Settings.TokensLimitsSettings).GetMethod(
            "OnSettingsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(settings, [settings.Settings, null]);
    }

    private static void RebuildEnabledSurfaces(TokensLimitsExtensionCommandsProvider provider)
    {
        var method = typeof(TokensLimitsExtensionCommandsProvider).GetMethod(
            "RebuildEnabledSurfaces",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(provider, null);
    }

    private static T[] GetPrivateSurfaceArray<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T[]>(field!.GetValue(instance));
    }

    private static int GetPrivateDictionaryCount(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<IDictionary>(field!.GetValue(instance));
        return dictionary.Count;
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(instance));
    }

    private static int GetStateChangedSubscriberCount(UsageSnapshotCache cache)
    {
        var field = typeof(UsageSnapshotCache).GetField("StateChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = field!.GetValue(cache) as Delegate;
        return handlers?.GetInvocationList().Length ?? 0;
    }

    private static bool HasStateChangedSubscriber(UsageSnapshotCache cache, object target)
    {
        var field = typeof(UsageSnapshotCache).GetField("StateChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = field!.GetValue(cache) as Delegate;
        return handlers?.GetInvocationList().Any(handler => ReferenceEquals(handler.Target, target)) ?? false;
    }

    private static T GetRegisteredSetting<T>(
        global::TokensLimitsExtension.Settings.TokensLimitsSettings settings,
        string key)
        where T : class
    {
        var field = typeof(Microsoft.CommandPalette.Extensions.Toolkit.Settings).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var registeredSettings = Assert.IsAssignableFrom<IDictionary>(field?.GetValue(settings.Settings));
        return Assert.IsType<T>(registeredSettings[key]);
    }
}
