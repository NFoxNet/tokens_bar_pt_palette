using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsagePaceEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Segment = Guid.NewGuid();

    [Fact]
    public void EstimatesPositiveConfirmedFixedWindowPace()
    {
        var estimate = UsagePaceEstimator.Estimate(Samples(), Now);

        Assert.NotNull(estimate);
        Assert.True(estimate.IsPrimaryWindow);
        Assert.Equal(40, estimate.PercentPerHour, 8);
        Assert.Equal(TimeSpan.FromHours(1.75), estimate.TimeUntilExhaustion);
        Assert.Equal(25, estimate.ExpectedPercentPerHour, 8);
        Assert.Equal(50, estimate.ExpectedUsedPercent, 8);
    }

    [Theory]
    [InlineData("few")]
    [InlineData("short")]
    [InlineData("stale")]
    [InlineData("gap")]
    [InlineData("estimate")]
    [InlineData("rolling")]
    [InlineData("unknown")]
    [InlineData("source")]
    [InlineData("segment")]
    [InlineData("provider")]
    [InlineData("reset")]
    [InlineData("decrease")]
    [InlineData("flat")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("nan")]
    public void RejectsInsufficientOrIncomparableMeasurements(string scenario)
    {
        var samples = Samples();
        samples = scenario switch
        {
            "few" => samples[..2],
            "short" => samples.Select((entry, index) => entry with { Timestamp = Now.AddMinutes(index - 2) }).ToArray(),
            "stale" => samples.Select(entry => entry with { Timestamp = entry.Timestamp.AddMinutes(-16) }).ToArray(),
            "gap" => [samples[0] with { Timestamp = Now.AddMinutes(-30) }, samples[1], samples[2]],
            "estimate" => [samples[0], samples[1] with { IsEstimate = true }, samples[2]],
            "rolling" => samples.Select(entry => entry with { PrimaryWindow = entry.PrimaryWindow! with { IsFixedWindow = false } }).ToArray(),
            "unknown" => samples.Select(entry => entry with { PrimaryWindow = entry.PrimaryWindow! with { StartsAt = null } }).ToArray(),
            "source" => [samples[0], samples[1] with { SourceKind = UsageHistorySourceKind.Local }, samples[2]],
            "segment" => [samples[0], samples[1] with { SegmentId = Guid.NewGuid() }, samples[2]],
            "provider" => [samples[0], samples[1] with { ProviderId = "other" }, samples[2]],
            "reset" => [samples[0], samples[1] with { PrimaryWindow = samples[1].PrimaryWindow! with { ResetAt = Now.AddHours(3) } }, samples[2]],
            "decrease" => [samples[0], samples[1] with { PrimaryWindow = samples[1].PrimaryWindow! with { UsedPercent = 10 } }, samples[2]],
            "flat" => samples.Select(entry => entry with { PrimaryWindow = entry.PrimaryWindow! with { UsedPercent = 20 } }).ToArray(),
            "future" => [samples[0], samples[1], samples[2] with { Timestamp = Now.AddMinutes(1) }],
            "expired" => samples.Select(entry => entry with { PrimaryWindow = entry.PrimaryWindow! with { ResetAt = Now } }).ToArray(),
            "nan" => [samples[0], samples[1] with { PrimaryWindow = samples[1].PrimaryWindow! with { UsedPercent = double.NaN } }, samples[2]],
            _ => throw new ArgumentException(scenario),
        };

        Assert.Null(UsagePaceEstimator.Estimate(samples, Now));
    }

    [Fact]
    public void UsesSecondaryWhenPrimaryIsRolling()
    {
        var samples = Samples().Select(entry => entry with
        {
            SecondaryWindow = entry.PrimaryWindow,
            PrimaryWindow = entry.PrimaryWindow! with { IsFixedWindow = false },
        }).ToArray();

        Assert.False(UsagePaceEstimator.Estimate(samples, Now)!.IsPrimaryWindow);
    }

    [Fact]
    public void StartsANewComparableSeriesAfterAnAccountBoundary()
    {
        var samples = Samples();
        var old = samples[0] with { Timestamp = Now.AddMinutes(-30), SegmentId = Guid.NewGuid() };

        Assert.NotNull(UsagePaceEstimator.Estimate([old, .. samples], Now));
    }

    [Fact]
    public void AccountsForTimeSinceTheLatestSample()
    {
        var estimate = UsagePaceEstimator.Estimate(Samples(), Now.AddMinutes(15));

        Assert.Equal(TimeSpan.FromHours(1.5), estimate!.TimeUntilExhaustion);
        Assert.Equal(56.25, estimate.ExpectedUsedPercent, 8);
    }

    [Fact]
    public void RestoresForecastOnlyAfterThreeContinuousSamplesFollowingAGap()
    {
        var samples = Samples();
        var old = samples[0] with { Timestamp = Now.AddHours(-1), PrimaryWindow = samples[0].PrimaryWindow! with { UsedPercent = 5 } };

        Assert.NotNull(UsagePaceEstimator.Estimate([old, .. samples], Now));
        Assert.Null(UsagePaceEstimator.Estimate([old, samples[1], samples[2]], Now));
    }

    [Theory]
    [InlineData("estimate")]
    [InlineData("rolling")]
    [InlineData("decrease")]
    public void StartsANewRunAfterAnIneligibleSampleWithoutFilteringAcrossIt(string scenario)
    {
        var samples = Samples();
        var old = samples[0] with { Timestamp = Now.AddMinutes(-25) };
        old = scenario switch
        {
            "estimate" => old with { IsEstimate = true },
            "rolling" => old with { PrimaryWindow = old.PrimaryWindow! with { IsFixedWindow = false } },
            "decrease" => old with { PrimaryWindow = old.PrimaryWindow! with { UsedPercent = 90 } },
            _ => throw new ArgumentException(scenario),
        };

        Assert.NotNull(UsagePaceEstimator.Estimate([old, .. samples], Now));
        Assert.Null(UsagePaceEstimator.Estimate([old, samples[1], samples[2]], Now));
    }
    private static UsageHistoryEntry[] Samples() => [Sample(-15, 20), Sample(-5, 25), Sample(0, 30)];

    private static UsageHistoryEntry Sample(int minutes, double used) => new(
        Now.AddMinutes(minutes), "codex", UsageHistorySourceKind.Api, Segment, false,
        new UsageHistoryWindow(used, Now.AddHours(-2), Now.AddHours(2), 14400, true), null, []);
}
