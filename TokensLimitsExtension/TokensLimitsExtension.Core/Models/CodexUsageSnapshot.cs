namespace TokensLimitsExtension.Core.Models;

public sealed record CodexUsageWindow(
    double UsedPercent,
    DateTimeOffset ResetAt,
    int LimitWindowSeconds);

public sealed record CodexAdditionalRateLimit(
    string Name,
    CodexUsageWindow? PrimaryWindow,
    CodexUsageWindow? SecondaryWindow);

public sealed record CodexUsageSnapshot(
    double PrimaryUsedPercent,
    DateTimeOffset PrimaryResetAt,
    double SecondaryUsedPercent,
    DateTimeOffset SecondaryResetAt,
    string? Plan,
    bool IsEstimate)
{
    public IReadOnlyList<CodexAdditionalRateLimit> AdditionalRateLimits { get; init; } = [];
}
