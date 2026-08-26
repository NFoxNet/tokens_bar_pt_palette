using System.Globalization;

namespace TokensLimitsExtension.Core.Services;

public static class CodexUsageNormalizer
{
    public static string FormatRemainingPercent(double usedPercent)
    {
        var remaining = Math.Clamp(100d - usedPercent, 0d, 100d);
        var rounded = (int)Math.Round(remaining, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{rounded}% осталось");
    }

    public static string FormatTimeUntilReset(DateTimeOffset resetAt, DateTimeOffset now)
    {
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "сброс уже прошёл";
        }

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes % (24 * 60)) / 60;
        var minutes = totalMinutes % 60;

        return days > 0
            ? $"через {days}д {hours}ч"
            : hours > 0
                ? minutes > 0 ? $"через {hours}ч {minutes}м" : $"через {hours}ч"
                : $"через {minutes}м";
    }
}
