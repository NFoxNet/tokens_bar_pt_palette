using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageNotificationEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmitsOneThresholdEventWhenFreshConfirmedWindowCrossesThreshold()
    {
        var evaluator = new UsageNotificationEvaluator();
        var initial = Snapshot(usedPercent: 80, resetAt: Now.AddHours(2), fetchedAt: Now.AddMinutes(-1));
        var crossing = Snapshot(usedPercent: 91, resetAt: Now.AddHours(2), fetchedAt: Now);

        Assert.Empty(evaluator.Evaluate("config-1", initial, Now, 10));
        var events = evaluator.Evaluate("config-1", crossing, Now, 10);
        var repeated = evaluator.Evaluate("config-1", crossing with { FetchedAt = Now.AddSeconds(1) }, Now.AddSeconds(1), 10);

        var notification = Assert.Single(events);
        Assert.Equal(UsageNotificationEventKind.ThresholdReached, notification.Kind);
        Assert.Equal(UsageNotificationWindow.Primary, notification.Window);
        Assert.Empty(repeated);
    }

    [Fact]
    public void DoesNotInventCrossingWhenFirstSnapshotIsAlreadyBelowThreshold()
    {
        var evaluator = new UsageNotificationEvaluator();

        var events = evaluator.Evaluate("config-1", Snapshot(usedPercent: 95, resetAt: Now.AddHours(2), fetchedAt: Now), Now, 10);

        Assert.Empty(events);
    }

    [Theory]
    [InlineData(true, 91)]
    [InlineData(false, double.NaN)]
    [InlineData(false, 101)]
    public void IgnoresEstimatedOrInvalidUsage(bool estimate, double usedPercent)
    {
        var evaluator = new UsageNotificationEvaluator();
        var initial = Snapshot(80, Now.AddHours(2), Now.AddMinutes(-1));
        var crossing = Snapshot(usedPercent, Now.AddHours(2), Now) with { IsEstimate = estimate };

        evaluator.Evaluate("config-1", initial, Now, 10);

        Assert.Empty(evaluator.Evaluate("config-1", crossing, Now, 10));
    }

    [Fact]
    public void DoesNotCompareAcrossSourceOrConfigurationSegments()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate("config-1", Snapshot(80, Now.AddHours(2), Now), Now, 10);

        Assert.Empty(evaluator.Evaluate(
            "config-2",
            Snapshot(95, Now.AddHours(2), Now.AddSeconds(1)),
            Now.AddSeconds(1),
            10));
        Assert.Empty(evaluator.Evaluate(
            "config-1",
            Snapshot(95, Now.AddHours(2), Now.AddSeconds(2)) with { Source = "local" },
            Now.AddSeconds(2),
            10));
    }

    [Fact]
    public void DoesNotCompareAcrossAccountScopesIncludingKnownToUnknown()
    {
        var evaluator = new UsageNotificationEvaluator();
        evaluator.Evaluate("config-1", Snapshot(80, Now.AddHours(2), Now) with { AccountScope = "acct-a" }, Now, 10);

        Assert.Empty(evaluator.Evaluate(
            "config-1",
            Snapshot(95, Now.AddHours(2), Now.AddSeconds(1)) with { AccountScope = "acct-b" },
            Now.AddSeconds(1),
            10));
        Assert.Empty(evaluator.Evaluate(
            "config-1",
            Snapshot(95, Now.AddHours(2), Now.AddSeconds(2)),
            Now.AddSeconds(2),
            10));
    }

    [Fact]
    public void EmitsResetOnlyForAConfirmedFixedWindowBoundary()
    {
        var evaluator = new UsageNotificationEvaluator();
        var oldWindow = FixedSnapshot(96, Now.AddHours(-5), Now.AddSeconds(-1), Now.AddMinutes(-1));
        var newWindow = FixedSnapshot(2, Now, Now.AddHours(5), Now);

        Assert.Empty(evaluator.Evaluate("config-1", oldWindow, Now, 10));
        var notification = Assert.Single(evaluator.Evaluate("config-1", newWindow, Now, 10));

        Assert.Equal(UsageNotificationEventKind.WindowReset, notification.Kind);
        Assert.Equal(UsageNotificationWindow.Primary, notification.Window);
    }

    [Fact]
    public void DoesNotTreatRollingWindowDropAsAReset()
    {
        var evaluator = new UsageNotificationEvaluator();
        var oldWindow = Snapshot(96, Now.AddHours(1), Now.AddMinutes(-1));
        var newWindow = Snapshot(2, Now.AddHours(2), Now);

        evaluator.Evaluate("config-1", oldWindow, Now, 10);

        Assert.Empty(evaluator.Evaluate("config-1", newWindow, Now, 10));
    }

    private static UsageSnapshot Snapshot(double usedPercent, DateTimeOffset resetAt, DateTimeOffset? fetchedAt)
        => new("codex", "Codex", new UsageWindow(usedPercent, resetAt, 18_000), null, "Plus", false)
        {
            Source = "api",
            FetchedAt = fetchedAt,
        };

    private static UsageSnapshot FixedSnapshot(
        double usedPercent,
        DateTimeOffset startsAt,
        DateTimeOffset resetAt,
        DateTimeOffset? fetchedAt)
        => Snapshot(usedPercent, resetAt, fetchedAt) with
        {
            PrimaryWindow = new UsageWindow(usedPercent, resetAt, 18_000)
            {
                StartsAt = startsAt,
                IsFixedWindow = true,
            },
        };
}
