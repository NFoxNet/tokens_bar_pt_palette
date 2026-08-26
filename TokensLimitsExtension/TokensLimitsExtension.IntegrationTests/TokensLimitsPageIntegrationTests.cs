using Microsoft.CommandPalette.Extensions;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Models;
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
    }

    private sealed class FakeUsageProvider(CodexUsageSnapshot snapshot) : ICodexUsageProvider
    {
        public Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }
}
