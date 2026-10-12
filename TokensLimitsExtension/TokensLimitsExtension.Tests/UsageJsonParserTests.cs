using System.Text;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Tests;

public sealed class UsageJsonParserTests
{
    [Fact]
    public void PreservesReplacementDecodingForMalformedUtf8Strings()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"plan\":\"Pro @\",\"usage\":7}");
        bytes[Array.IndexOf(bytes, (byte)'@')] = 0xff;

        var snapshot = UsageJsonParser.ParseUtf8(
            UsageProviderDescriptorRegistry.All.Single(descriptor => descriptor.Id == "groq"),
            "test",
            bytes,
            DateTimeOffset.UtcNow);

        Assert.Equal("Pro \uFFFD", snapshot.Plan);
    }

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
    [Theory]
    [InlineData("fixed", true)]
    [InlineData("cycle", true)]
    [InlineData("rolling", false)]
    [InlineData("monthly", false)]
    [InlineData("unknown", false)]
    public void RequiresExplicitFixedPeriodEvidence(string windowType, bool expectedFixed)
    {
        var snapshot = UsageJsonParser.ParseText(UsageProviderDescriptorRegistry.Codex, "test",
            $$"""
            { "hourly": { "used_percent": 30, "window_type": "{{windowType}}",
              "starts_at": "2030-01-01T00:00:00Z", "reset_at": "2030-01-01T01:00:00Z",
              "window_seconds": 3600 } }
            """, DateTimeOffset.UtcNow);

        var window = Assert.IsType<TokensLimitsExtension.Core.Models.UsageWindow>(snapshot.PrimaryWindow);
        Assert.Equal(expectedFixed, window.IsFixedWindow);
        Assert.Equal(expectedFixed ? new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null, window.StartsAt);
    }

    [Theory]
    [InlineData("2030-01-01T01:00:00Z")]
    [InlineData("2030-01-01T02:00:00Z")]
    [InlineData("invalid")]
    public void RejectsInvalidFixedPeriodBoundaries(string start)
    {
        var snapshot = UsageJsonParser.ParseText(UsageProviderDescriptorRegistry.Codex, "test",
            $$"""
            { "hourly": { "used_percent": 30, "type": "fixed", "starts_at": "{{start}}",
              "reset_at": "2030-01-01T01:00:00Z", "window_seconds": 3600 } }
            """, DateTimeOffset.UtcNow);

        Assert.False(snapshot.PrimaryWindow!.IsFixedWindow);
    }

    [Fact]
    public void DoesNotTreatAnInheritedResetAsFixedPeriodEvidence()
    {
        var snapshot = UsageJsonParser.ParseText(UsageProviderDescriptorRegistry.Codex, "test",
            """
            { "reset_at": "2030-01-01T01:00:00Z", "hourly": { "used_percent": 30,
              "type": "fixed", "starts_at": "2030-01-01T00:00:00Z", "window_seconds": 3600 } }
            """, DateTimeOffset.UtcNow);

        Assert.False(snapshot.PrimaryWindow!.IsFixedWindow);
    }
    [Fact]
    public void DerivesDeclaredCycleDurationFromItsExplicitBoundaries()
    {
        var snapshot = UsageJsonParser.ParseText(UsageProviderDescriptorRegistry.Codex, "test",
            """
            { "quota": { "used_percent": 30, "window_type": "cycle",
              "currentPeriodStart": "2030-01-01T00:00:00Z", "currentPeriodEnd": "2030-01-01T01:00:00Z" } }
            """, DateTimeOffset.UtcNow);

        Assert.True(snapshot.PrimaryWindow!.IsFixedWindow);
        Assert.Equal(3600, snapshot.PrimaryWindow.LimitWindowSeconds);
    }
}
