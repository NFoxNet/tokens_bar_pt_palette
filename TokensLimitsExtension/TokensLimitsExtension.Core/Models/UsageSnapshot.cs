namespace TokensLimitsExtension.Core.Models;

public sealed record UsageWindow(
    double UsedPercent,
    DateTimeOffset ResetAt,
    int LimitWindowSeconds);

public sealed record AdditionalUsageLimit(
    string Name,
    UsageWindow? PrimaryWindow,
    UsageWindow? SecondaryWindow);

public sealed record UsageSnapshot(
    string ProviderId,
    string ProviderDisplayName,
    UsageWindow PrimaryWindow,
    UsageWindow SecondaryWindow,
    string? Plan,
    bool IsEstimate)
{
    public IReadOnlyList<AdditionalUsageLimit> AdditionalRateLimits { get; init; } = [];
}
