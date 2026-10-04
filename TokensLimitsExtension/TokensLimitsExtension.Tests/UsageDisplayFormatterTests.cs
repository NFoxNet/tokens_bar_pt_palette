using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageDisplayFormatterTests
{
    private static readonly ILocalizationService Localization = InvariantLocalizationService.Instance;

    [Theory]
    [InlineData(3_600, "1ч")]
    [InlineData(14_400, "4ч")]
    [InlineData(18_000, "5ч")]
    [InlineData(86_400, "1д")]
    [InlineData(604_800, "7д")]
    [InlineData(2_592_000, "30д")]
    public void FormatDockBandSubtitleUsesActualWindowDuration(int seconds, string expectedLabel)
    {
        var snapshot = Snapshot(primary: Window(10, seconds));

        Assert.Equal($"{expectedLabel}\\90%", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitleUsesSecondaryWindowWhenPrimaryIsMissing()
    {
        var snapshot = Snapshot(secondary: Window(25, 14_400));

        Assert.Equal("4ч\\75%", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitleUsesSemanticLabelsForUnknownWindowDurations()
    {
        var snapshot = Snapshot(primary: Window(10, 123));

        Assert.Equal("Основное\\90%", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitleDoesNotRenderMissingWindowSlots()
    {
        var snapshot = Snapshot(primary: Window(10, 18_000));

        Assert.Equal("5ч\\90%", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot));
    }

    [Fact]
    public void FormatDockBandSubtitleKeepsLegacyNullLocalizationAndActualDuration()
    {
        var snapshot = Snapshot(primary: Window(10, 14_400), secondary: Window(50, 2_592_000));

        Assert.Equal("4ч\\90%, 30д\\50%", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot));
    }

    [Fact]
    public void FormatDockBandSubtitleDoesNotInventQuotaForMetricsOnlySnapshot()
    {
        var snapshot = Snapshot(metrics: [Metric("Token balance", "42", "credits", "totalBalance")]);

        Assert.Equal("Total balance: 42 credits", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitleAddsBalanceToWindowsWithoutReplacingThem()
    {
        var snapshot = Snapshot(
            primary: Window(20, 18_000),
            secondary: Window(40, 604_800),
            metrics: [Metric("Token balance", "12.5", "USD", "totalBalance", 12.5m, "USD")]);

        Assert.Equal("5ч\\80%, 7д\\60%, Total balance: 12.5 USD", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitleOrdersMetricsBySemanticValueAndBoundsLongValues()
    {
        var snapshot = Snapshot(metrics:
        [
            Metric("Balance", "balance-value", null, "totalBalance"),
            Metric("Weekly tokens", "weekly-value", null, "tokens7d"),
            Metric("Five hour tokens", new string('x', 80), null, "tokens5h"),
        ]);

        var result = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization);

        Assert.StartsWith("Токены за 5 часов: ", result);
        Assert.Contains("Токены за 7 дней: weekly-value", result);
        Assert.DoesNotContain("Balance:", result);
        Assert.True(result.Length < 100);
    }

    [Theory]
    [InlineData(false, "5ч\\80%, 7д\\60%, Total balance: 10 USD")]
    [InlineData(true, "Оценка: 5ч\\80%, 7д\\60%, Total balance: 10 USD")]
    public void FormatDockBandSubtitlePrefixesEstimateForWindowAndBalanceBranch(bool isEstimate, string expected)
    {
        var snapshot = Snapshot(
            primary: Window(20, 18_000),
            secondary: Window(40, 604_800),
            metrics: [Metric("Balance", "10", "USD", "totalBalance")],
            isEstimate: isEstimate);

        Assert.Equal(expected, UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    [Fact]
    public void FormatDockBandSubtitlePrefixesEstimateForMetricsOnlyBranch()
    {
        var snapshot = Snapshot(
            metrics: [Metric("Balance", "10", "USD", "totalBalance")],
            isEstimate: true);

        Assert.Equal("Оценка: Total balance: 10 USD", UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, Localization));
    }

    private static UsageSnapshot Snapshot(
        UsageWindow? primary = null,
        UsageWindow? secondary = null,
        IReadOnlyList<UsageMetric>? metrics = null,
        bool isEstimate = false)
        => new("provider", "Provider", primary, secondary, null, isEstimate)
        {
            Metrics = metrics ?? [],
        };

    private static UsageWindow Window(double usedPercent, int seconds)
        => new(usedPercent, DateTimeOffset.UnixEpoch.AddDays(1), seconds);

    private static UsageMetric Metric(
        string name,
        string value,
        string? unit,
        string semanticKey,
        decimal? numericValue = null,
        string? currency = null)
        => new(name, value, unit, SemanticKey: semanticKey, NumericValue: numericValue, CurrencyCode: currency);
}
