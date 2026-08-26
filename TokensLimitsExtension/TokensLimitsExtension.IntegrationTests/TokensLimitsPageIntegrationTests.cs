using Microsoft.CommandPalette.Extensions;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsPageIntegrationTests
{
    [Fact]
    public void PageReturnsTwoLimitItemsWithExpectedTitles()
    {
        var now = DateTimeOffset.UtcNow;
        using var page = new TokensLimitsPage(new FakeUsageProvider(
            new CodexUsageSnapshot(38, now.AddHours(1), 12, now.AddDays(2), "pro", false)));

        var items = page.GetItems();

        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.Title.Contains("5-часовой", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Title.Contains("Недельный", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Subtitle.Contains("62% осталось", StringComparison.Ordinal));
    }

    [Fact]
    public void CommandsProviderExposesExactlyOneCommand()
    {
        var provider = new TokensLimitsExtensionCommandsProvider(new FakeUsageProvider(
            new CodexUsageSnapshot(1, DateTimeOffset.UtcNow, 2, DateTimeOffset.UtcNow, null, true)));

        Assert.Single(provider.TopLevelCommands());
        Assert.Equal("com.tokenslimits.extension", provider.Id);
    }

    [Fact]
    public void DockBandsAreCreatedFromTheProviderRegistry()
    {
        var otherProvider = new FakeGenericProvider(
            "other-provider",
            "Other Provider",
            new UsageSnapshot(
                "other-provider",
                "Other Provider",
                new UsageWindow(10, DateTimeOffset.UtcNow.AddHours(1), 3600),
                new UsageWindow(20, DateTimeOffset.UtcNow.AddDays(1), 86400),
                null,
                false));
        using var provider = new TokensLimitsExtensionCommandsProvider(
            new FakeUsageProvider(new CodexUsageSnapshot(
                1,
                DateTimeOffset.UtcNow.AddHours(1),
                2,
                DateTimeOffset.UtcNow.AddDays(1),
                null,
                true)),
            new UsageProviderRegistry([otherProvider]));

        var bands = provider.GetDockBands();

        var band = Assert.Single(bands);
        Assert.Equal("com.tokenslimits.provider.other-provider.band", band.Command!.Id);
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
        Assert.Contains("62% осталось", item.Subtitle, StringComparison.Ordinal);
        Assert.Contains("88% осталось", item.Subtitle, StringComparison.Ordinal);
        Assert.Contains("сброс", item.Subtitle, StringComparison.Ordinal);
        item.Dispose();
        Assert.True(item.IsDisposed);
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
}
