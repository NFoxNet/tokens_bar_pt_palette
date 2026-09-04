using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Tests;

public sealed class UsageJsonParserTests
{
    [Fact]
    public void NormalizesDeepSeekBalanceWithCurrencyInsteadOfPickingJsonFieldOrder()
    {
        var descriptor = UsageProviderDescriptorRegistry.All.Single(item => item.Id == "deepseek");
        var snapshot = UsageJsonParser.ParseText(
            descriptor,
            "balance",
            """
            {
              "is_available": true,
              "balance_infos": [
                { "currency": "USD", "granted_balance": "0", "total_balance": "8.95", "topped_up_balance": "8.95" }
              ]
            }
            """,
            DateTimeOffset.UtcNow);

        var balance = Assert.Single(snapshot.Metrics);
        Assert.Equal("totalBalance", balance.SemanticKey);
        Assert.Equal(8.95m, balance.NumericValue);
        Assert.Equal("USD", balance.CurrencyCode);
    }

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
