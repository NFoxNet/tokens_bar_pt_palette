using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsPageIntegrationTests
{
    [Fact]
    public async Task PageReturnsDetailedLimitItemsWithExpectedTitles()
    {
        var now = DateTimeOffset.UtcNow;
        using var page = new TokensLimitsPage(new FakeUsageProvider(
            new CodexUsageSnapshot(38, now.AddHours(1), 12, now.AddDays(2), "pro", false)));

        await page.RefreshAsync();
        var items = page.GetItems();

        Assert.Equal(3, items.Length);
        Assert.Contains(items, item => item.Title == "5ч");
        Assert.Contains(items, item => item.Title == "Еженедельно");
        Assert.Contains(items, item => item.Subtitle.Contains("62% осталось", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Subtitle.Contains("через", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Title == "План" && item.Subtitle == "pro");
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

    private sealed class FakeUsageProvider(CodexUsageSnapshot snapshot) : ICodexUsageProvider
    {
        public Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FakeGenericProvider(
        string id,
        string displayName,
        UsageSnapshot snapshot) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class MutableGenericProvider(string id, string displayName, UsageSnapshot snapshot) : IUsageProvider
    {
        private UsageSnapshot _snapshot = snapshot;

        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Volatile.Read(ref _snapshot));

        public void SetSnapshot(UsageSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
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
}
