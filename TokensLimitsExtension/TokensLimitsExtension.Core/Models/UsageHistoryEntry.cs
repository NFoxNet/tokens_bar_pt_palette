namespace TokensLimitsExtension.Core.Models;

public enum UsageHistorySourceKind
{
    Unknown,
    Api,
    Local,
}

public sealed record UsageHistoryWindow(
    double UsedPercent,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ResetAt,
    int LimitWindowSeconds,
    bool IsFixedWindow)
{
    public bool IsForecastEligible
        => IsFixedWindow
            && StartsAt is { } startsAt
            && ResetAt is { } resetAt
            && resetAt > startsAt
            && LimitWindowSeconds > 0;
}

public sealed record UsageHistoryMetric(
    string SemanticKey,
    decimal? NumericValue = null,
    double? Used = null,
    double? Limit = null,
    double? Remaining = null);

public sealed record UsageHistoryEntry(
    DateTimeOffset Timestamp,
    string ProviderId,
    UsageHistorySourceKind SourceKind,
    Guid SegmentId,
    bool IsEstimate,
    UsageHistoryWindow? PrimaryWindow,
    UsageHistoryWindow? SecondaryWindow,
    IReadOnlyList<UsageHistoryMetric> Metrics)
{
    public bool IsForecastEligible
        => !IsEstimate
            && (PrimaryWindow?.IsForecastEligible == true || SecondaryWindow?.IsForecastEligible == true);
}
