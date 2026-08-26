using System.Globalization;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public static class UsageDisplayFormatter
{
    public static string FormatRemainingPercent(double usedPercent)
    {
        var rounded = GetRemainingPercent(usedPercent);
        return string.Create(CultureInfo.InvariantCulture, $"{rounded}% осталось");
    }

    public static string FormatDockBandSubtitle(UsageSnapshot snapshot)
    {
        var estimatePrefix = snapshot.IsEstimate ? "Оценка: " : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{estimatePrefix}5ч\\{FormatDockPercent(snapshot.PrimaryWindow)}, 7д\\{FormatDockPercent(snapshot.SecondaryWindow)}");
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

    public static string FormatCompactBandSubtitle(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var estimatePrefix = snapshot.IsEstimate ? "Оценка: " : string.Empty;
        return $"{estimatePrefix}5ч: {FormatRemainingWindow(snapshot.PrimaryWindow, now)} · "
            + $"нед: {FormatRemainingWindow(snapshot.SecondaryWindow, now)}";
    }

    public static string FormatRemainingWindow(UsageWindow? window, DateTimeOffset now)
        => window is null
            ? "данные недоступны"
            : $"{FormatRemainingPercent(window.UsedPercent)} · {FormatTimeUntilReset(window.ResetAt, now)}";

    private static int GetRemainingPercent(double usedPercent)
    {
        var remaining = Math.Clamp(100d - usedPercent, 0d, 100d);
        return (int)Math.Round(remaining, MidpointRounding.AwayFromZero);
    }

    private static string FormatDockPercent(UsageWindow? window)
    {
        if (window is null)
        {
            return "—";
        }

        var remaining = Math.Clamp(100d - window.UsedPercent, 0d, 100d);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(remaining, MidpointRounding.AwayFromZero)}%");
    }
}
