using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class UsageHistoryPageTests
{
    [Fact]
    public void PageShowsRecentSafeSamplesAndExplicitClearAction()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokens-history-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new UsageHistoryStore(directory);
            var segment = Guid.NewGuid();
            var startsAt = now.AddHours(-1);
            var resetsAt = now.AddHours(4);
            Assert.True(store.Append(new UsageHistoryEntry(
                now.AddMinutes(-20),
                "codex",
                UsageHistorySourceKind.Api,
                segment,
                false,
                new UsageHistoryWindow(28, startsAt, resetsAt, 18_000, true),
                null,
                [new UsageHistoryMetric("tokens5h", 900)])));
            Assert.True(store.Append(new UsageHistoryEntry(
                now.AddMinutes(-10),
                "codex",
                UsageHistorySourceKind.Api,
                segment,
                false,
                new UsageHistoryWindow(30, startsAt, resetsAt, 18_000, true),
                null,
                [new UsageHistoryMetric("tokens5h", 1000)])));
            Assert.True(store.Append(new UsageHistoryEntry(
                now,
                "codex",
                UsageHistorySourceKind.Api,
                segment,
                false,
                new UsageHistoryWindow(31, startsAt, resetsAt, 18_000, true),
                null,
                [new UsageHistoryMetric("tokens5h", 1200)])));
            using var cache = new UsageSnapshotCache(new StubProvider());
            using var controller = new UsageHistoryController(store, []);
            using var page = new UsageHistoryPage(controller, [cache]);

            var items = page.GetItems();
            Assert.Contains(items, item => item.Title == "Clear history" && item.Command is AnonymousCommand);
            Assert.Contains(items, item => item.Title == "Codex · 31%" && item.Subtitle.Contains("API", StringComparison.Ordinal));
            var latestSample = Assert.IsType<ListItem>(Assert.Single(items, item => item.Title == "Codex · 31%"));
            Assert.Contains("Series ", latestSample.Details?.Body, StringComparison.Ordinal);
            Assert.Contains("tokens5h: 1200", latestSample.Details?.Body, StringComparison.Ordinal);
            Assert.Contains(items, item => item.Title == "Usage pace · Codex" && item.Subtitle.Contains("no exhaustion is expected before reset", StringComparison.Ordinal));
            var pace = Assert.IsType<ListItem>(Assert.Single(items, item => item.Title == "Usage pace · Codex"));
            Assert.Contains("Usage now: 31%", pace.Details?.Body, StringComparison.Ordinal);
            Assert.Contains("even pace by now:", pace.Details?.Body, StringComparison.Ordinal);

            Assert.IsType<AnonymousCommand>(items[0].Command).Invoke();
            Assert.Empty(store.Read("codex"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

            Assert.Contains(page.GetItems(), item => item.Title == "History could not be read");
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
