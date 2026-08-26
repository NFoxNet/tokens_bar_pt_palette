using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageProviderRegistryTests
{
    [Fact]
    public void KeepsProviderOrderAndFindsById()
    {
        var first = new FakeProvider("first", "First");
        var second = new FakeProvider("second", "Second");
        var registry = new UsageProviderRegistry([first, second]);

        Assert.Equal([first, second], registry.Providers);
        Assert.Same(second, registry.Find("SECOND"));
    }

    [Fact]
    public void RejectsDuplicateProviderIds()
    {
        var first = new FakeProvider("same", "First");
        var second = new FakeProvider("SAME", "Second");

        var exception = Assert.Throws<ArgumentException>(() => new UsageProviderRegistry([first, second]));

        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexAdapterMapsExistingSnapshotToCommonContract()
    {
        var primaryResetAt = DateTimeOffset.UtcNow.AddHours(2);
        var secondaryResetAt = DateTimeOffset.UtcNow.AddDays(3);
        var codexSnapshot = new CodexUsageSnapshot(
            38,
            primaryResetAt,
            12,
            secondaryResetAt,
            "pro",
            false)
        {
            AdditionalRateLimits =
            [
                new CodexAdditionalRateLimit(
                    "Codex extra",
                    new CodexUsageWindow(25, primaryResetAt, 1800),
                    null),
            ],
        };
        var adapter = new CodexUsageProviderAdapter(new FakeCodexProvider(codexSnapshot));

        var snapshot = await adapter.GetUsageSnapshotAsync();

        Assert.Equal("codex", snapshot.ProviderId);
        Assert.Equal("Codex", snapshot.ProviderDisplayName);
        Assert.Equal(38, snapshot.PrimaryWindow.UsedPercent);
        Assert.Equal(5 * 60 * 60, snapshot.PrimaryWindow.LimitWindowSeconds);
        Assert.Equal(7 * 24 * 60 * 60, snapshot.SecondaryWindow.LimitWindowSeconds);
        Assert.Equal("pro", snapshot.Plan);
        Assert.False(snapshot.IsEstimate);
        var additional = Assert.Single(snapshot.AdditionalRateLimits);
        Assert.Equal("Codex extra", additional.Name);
        Assert.Equal(1800, additional.PrimaryWindow!.LimitWindowSeconds);
        Assert.Null(additional.SecondaryWindow);
    }

    private sealed class FakeProvider(string id, string displayName) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = new(id, displayName);

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UsageSnapshot(
                Descriptor.Id,
                Descriptor.DisplayName,
                new UsageWindow(0, DateTimeOffset.UtcNow, 0),
                new UsageWindow(0, DateTimeOffset.UtcNow, 0),
                null,
                false));
    }

    private sealed class FakeCodexProvider(CodexUsageSnapshot snapshot) : ICodexUsageProvider
    {
        public Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }
}
