using System.Globalization;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public static class UsageDisplayFormatter
{
    public static string FormatRemainingPercent(double usedPercent, ILocalizationService? localization = null)
    {
        var rounded = GetRemainingPercent(usedPercent);
        if (localization is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{rounded}% осталось");
        }

        return localization.Format("status.remaining", rounded);
    }

    public static string FormatDockBandSubtitle(UsageSnapshot snapshot, ILocalizationService? localization = null)
    {
        if (localization is null)
        {
            return $"5ч\\{FormatDockPercent(snapshot.PrimaryWindow)}, 7д\\{FormatDockPercent(snapshot.SecondaryWindow)}";
        }

        var estimatePrefix = snapshot.IsEstimate ? localization.GetString("status.estimate", "Estimate: ") : string.Empty;
        var totalBalance = snapshot.Metrics.FirstOrDefault(metric =>
            string.Equals(metric.SemanticKey, "totalBalance", StringComparison.OrdinalIgnoreCase));
        if (totalBalance is not null)
        {
            return $"{localization.GetString("metrics.totalBalance", "Total balance")}: {FormatMetric(totalBalance, localization.Culture)}";
        }
        if (snapshot.PrimaryWindow is null
            && snapshot.SecondaryWindow is null
            && snapshot.Metrics.Count > 0)
        {
            var metrics = snapshot.Metrics
                .Take(2)
                .Select(metric => $"{metric.Name}: {TrimMetricValue(FormatMetric(metric, localization.Culture))}");
            return estimatePrefix + string.Join(", ", metrics);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{estimatePrefix}{localization.Format("time.hours", 5)}\\{FormatDockPercent(snapshot.PrimaryWindow)}, {localization.Format("time.days", 7)}\\{FormatDockPercent(snapshot.SecondaryWindow)}");
    }

    public static string FormatTimeUntilReset(DateTimeOffset resetAt, DateTimeOffset now, ILocalizationService? localization = null)
    {
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return localization is null
                ? "сброс уже прошёл"
                : localization.GetString("status.resetPassed", "reset passed");
        }

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes % (24 * 60)) / 60;
        var minutes = totalMinutes % 60;

        if (localization is null)
        {
            return days > 0
                ? $"через {days}д {hours}ч"
                : hours > 0
                    ? minutes > 0 ? $"через {hours}ч {minutes}м" : $"через {hours}ч"
                    : $"через {minutes}м";
        }

        var duration = days > 0
            ? $"{localization.Format("time.days", days)} {localization.Format("time.hours", hours)}"
            : hours > 0
                ? minutes > 0
                    ? $"{localization.Format("time.hours", hours)} {localization.Format("time.minutes", minutes)}"
                    : localization.Format("time.hours", hours)
                : localization.Format("time.minutes", minutes);
        return localization.Format("status.reset", duration);
    }

    public static string FormatCompactBandSubtitle(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var estimatePrefix = snapshot.IsEstimate ? "Оценка: " : string.Empty;
        return $"{estimatePrefix}5ч: {FormatRemainingWindow(snapshot.PrimaryWindow, now)} · "
            + $"нед: {FormatRemainingWindow(snapshot.SecondaryWindow, now)}";
    }

    public static string FormatRemainingWindow(UsageWindow? window, DateTimeOffset now, ILocalizationService? localization = null)
        => window is null
            ? localization is null
                ? "данные недоступны"
                : localization.GetString("overview.unavailable", "Data unavailable")
            : $"{FormatRemainingPercent(window.UsedPercent, localization)} · {FormatTimeUntilReset(window.ResetAt, now, localization)}";

    public static string GetWindowLabel(UsageWindow? window, string fallback, ILocalizationService? localization = null)
        => window is null ? fallback : localization is null
            ? GetLegacyWindowShortLabel(window, fallback)
            : GetWindowShortLabel(window, fallback, localization);

    private static string GetLegacyWindowShortLabel(UsageWindow? window, string fallback)
    {
        if (window is null || window.LimitWindowSeconds <= 0)
        {
            return fallback;
        }

        var seconds = window.LimitWindowSeconds;
        if (seconds % (7 * 24 * 60 * 60) == 0)
        {
            var weeks = seconds / (7 * 24 * 60 * 60);
            return weeks == 1 ? "7д" : $"{weeks}н";
        }

        if (seconds % (24 * 60 * 60) == 0) return $"{seconds / (24 * 60 * 60)}д";
        if (seconds % (60 * 60) == 0) return $"{seconds / (60 * 60)}ч";
        return $"{Math.Max(1, seconds / 60)}м";
    }

    private static string GetWindowShortLabel(UsageWindow? window, string fallback, ILocalizationService localization)
    {
        if (window is null || window.LimitWindowSeconds <= 0)
        {
            return fallback;
        }

        var seconds = window.LimitWindowSeconds;
        if (seconds % (7 * 24 * 60 * 60) == 0)
        {
            var weeks = seconds / (7 * 24 * 60 * 60);
            return weeks == 1 ? localization.Format("time.days", 7) : localization.Format("time.days", weeks * 7);
        }

        if (seconds % (24 * 60 * 60) == 0)
        {
            return localization.Format("time.days", seconds / (24 * 60 * 60));
        }

        if (seconds % (60 * 60) == 0)
        {
            return localization.Format("time.hours", seconds / (60 * 60));
        }

        return localization.Format("time.minutes", Math.Max(1, seconds / 60));
    }

    private static string TrimMetricValue(string value)
        => value.Length <= 24 ? value : value[..24] + "…";

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

    public static string FormatMetric(UsageMetric metric, CultureInfo culture)
    {
        if (metric.NumericValue is decimal value)
        {
            var formatted = value.ToString("0.##", culture);
            return string.IsNullOrWhiteSpace(metric.CurrencyCode)
                ? formatted
                : $"{formatted} {metric.CurrencyCode}";
        }

        return metric.Unit is null ? metric.Value : $"{metric.Value} {metric.Unit}";
    }
}
