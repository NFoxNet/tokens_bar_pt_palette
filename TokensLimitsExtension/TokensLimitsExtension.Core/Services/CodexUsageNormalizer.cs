namespace TokensLimitsExtension.Core.Services;

public static class CodexUsageNormalizer
{
    public static string FormatRemainingPercent(double usedPercent)
        => UsageDisplayFormatter.FormatRemainingPercent(usedPercent);

    public static string FormatTimeUntilReset(DateTimeOffset resetAt, DateTimeOffset now)
        => UsageDisplayFormatter.FormatTimeUntilReset(resetAt, now);
}
