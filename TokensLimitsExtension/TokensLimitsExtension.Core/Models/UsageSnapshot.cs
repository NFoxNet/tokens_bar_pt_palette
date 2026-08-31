namespace TokensLimitsExtension.Core.Models;

public sealed record UsageWindow(
    double UsedPercent,
    DateTimeOffset ResetAt,
    int LimitWindowSeconds);

public sealed record AdditionalUsageLimit(
    string Name,
    UsageWindow? PrimaryWindow,
    UsageWindow? SecondaryWindow);

/// <summary>
/// A provider-specific value that does not fit the standard rolling-window pair.
/// Examples are token counts, spend, credit balances, and model-specific quotas.
/// Values are kept as received from the provider; no estimated percentage is
/// invented when the provider does not publish a limit.
/// </summary>
public sealed record UsageMetric(
    string Name,
    string Value,
    string? Unit = null,
    double? Used = null,
    double? Limit = null,
    double? Remaining = null,
    DateTimeOffset? ResetAt = null);

public sealed record UsageSnapshot(
    string ProviderId,
    string ProviderDisplayName,
    UsageWindow? PrimaryWindow,
    UsageWindow? SecondaryWindow,
    string? Plan,
    bool IsEstimate)
{
    public IReadOnlyList<AdditionalUsageLimit> AdditionalRateLimits { get; init; } = [];

    public IReadOnlyList<UsageMetric> Metrics { get; init; } = [];

    public DateTimeOffset? FetchedAt { get; init; }

    public string? Source { get; init; }
}
