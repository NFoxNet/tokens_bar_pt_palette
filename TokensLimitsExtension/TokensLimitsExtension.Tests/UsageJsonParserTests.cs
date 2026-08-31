using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Tests;

public sealed class UsageJsonParserTests
{
    [Fact]
    public void KeepsOnePercentAsOnePercentAndDoesNotDuplicateASingleWindow()
    {
        var snapshot = UsageJsonParser.ParseText(
            UsageProviderDescriptorRegistry.Codex,
            "test",
            """
            { "weekly": { "used_percent": 1, "reset_at": "2030-01-01T00:00:00Z", "window_seconds": 604800 } }
            """,
            DateTimeOffset.UtcNow);

        Assert.Null(snapshot.PrimaryWindow);
        Assert.NotNull(snapshot.SecondaryWindow);
        Assert.Equal(1, snapshot.SecondaryWindow.UsedPercent);
    }

    [Fact]
    public void ConvertsOnePercentRemainingToNinetyNinePercentUsed()
    {
        var snapshot = UsageJsonParser.ParseText(
            UsageProviderDescriptorRegistry.Codex,
            "test",
            """
            { "weekly": { "remaining_percent": 1, "reset_at": "2030-01-01T00:00:00Z", "window_seconds": 604800 } }
            """,
            DateTimeOffset.UtcNow);

        Assert.Null(snapshot.PrimaryWindow);
        Assert.NotNull(snapshot.SecondaryWindow);
        Assert.Equal(99, snapshot.SecondaryWindow.UsedPercent);
    }
}
