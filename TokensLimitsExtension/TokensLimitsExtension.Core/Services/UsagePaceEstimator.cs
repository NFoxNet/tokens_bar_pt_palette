using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public sealed record UsagePaceEstimate(
    bool IsPrimaryWindow,
    double PercentPerHour,
    TimeSpan TimeUntilExhaustion,
    double ExpectedPercentPerHour,
    double ExpectedUsedPercent);

public static class UsagePaceEstimator
{
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(15);

    public static UsagePaceEstimate? Estimate(IReadOnlyList<UsageHistoryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count < 3 || entries.Count > UsageHistoryStore.MaximumEntriesPerProvider)
        {
            return null;
        }

        return EstimateWindow(entries, now, primary: true) ?? EstimateWindow(entries, now, primary: false);
    }

    private static UsagePaceEstimate? EstimateWindow(IReadOnlyList<UsageHistoryEntry> entries, DateTimeOffset now, bool primary)
    {
        var latest = entries[^1];
        var window = GetWindow(latest, primary);
        if (window?.IsForecastEligible != true || latest.IsEstimate || latest.SourceKind != UsageHistorySourceKind.Api
            || now < latest.Timestamp || now - latest.Timestamp > MaximumGap
            || now >= window.ResetAt || now < window.StartsAt)
        {
            return null;
        }

        var first = latest;
        var previous = latest;
        var count = 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            var candidate = GetWindow(entry, primary);
            // A reset or account/source change begins a new series; never bridge that boundary.
            if (entry.ProviderId != latest.ProviderId || entry.SourceKind != latest.SourceKind
                || entry.SegmentId != latest.SegmentId || candidate?.StartsAt != window.StartsAt
                || candidate?.ResetAt != window.ResetAt || candidate?.LimitWindowSeconds != window.LimitWindowSeconds)
            {
                break;
            }

            if (entry.IsEstimate || candidate?.IsForecastEligible != true
                || !double.IsFinite(candidate.UsedPercent) || candidate.UsedPercent is < 0 or > 100
                || entry.Timestamp < window.StartsAt || entry.Timestamp >= window.ResetAt
                || (index < entries.Count - 1 && (entry.Timestamp >= previous.Timestamp
                    || previous.Timestamp - entry.Timestamp > MaximumGap
                    || candidate.UsedPercent > GetWindow(previous, primary)!.UsedPercent)))
            {
                break;
            }

            first = entry;
            previous = entry;
            count++;
        }

        var span = latest.Timestamp - first.Timestamp;
        if (count < 3 || span < MinimumSpan)
        {
            return null;
        }

        var rate = (window.UsedPercent - GetWindow(first, primary)!.UsedPercent) / span.TotalHours;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            return null;
        }

        var remaining = Math.Max(0, 100 - window.UsedPercent - rate * (now - latest.Timestamp).TotalHours);
        var exhaustionSeconds = remaining / rate * 3600;
        if (!double.IsFinite(exhaustionSeconds) || exhaustionSeconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        var duration = (window.ResetAt!.Value - window.StartsAt!.Value).TotalHours;
        return new UsagePaceEstimate(primary, rate, TimeSpan.FromSeconds(exhaustionSeconds),
            100 / duration, 100 * (now - window.StartsAt.Value).TotalHours / duration);
    }

    private static UsageHistoryWindow? GetWindow(UsageHistoryEntry entry, bool primary)
        => primary ? entry.PrimaryWindow : entry.SecondaryWindow;
}
