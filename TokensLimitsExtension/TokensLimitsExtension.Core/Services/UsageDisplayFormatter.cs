using System.Globalization;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public static class UsageDisplayFormatter
{
    public static string GetMetricName(UsageMetric metric, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(localization);
        return metric.SemanticKey?.ToLowerInvariant() switch
        {
            "tokens5h" => localization.GetString("metrics.tokens5h", metric.Name),
            "tokens7d" => localization.GetString("metrics.tokens7d", metric.Name),
            "totalbalance" => localization.GetString("metrics.totalBalance", metric.Name),
            _ => metric.Name,
        };
    }

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
        ArgumentNullException.ThrowIfNull(snapshot);

        var estimatePrefix = snapshot.IsEstimate
            ? localization?.GetString("status.estimate", "Estimate: ") ?? "Оценка: "
            : string.Empty;
        var metrics = GetPrioritizedMetrics(snapshot.Metrics);
        if (snapshot.PrimaryWindow is null && snapshot.SecondaryWindow is null)
        {
            if (metrics.Length == 0)
            {
                return localization?.GetString("status.unavailable", "Usage unavailable") ?? "Лимиты недоступны";
            }

            return estimatePrefix + string.Join(", ", metrics.Take(2).Select(metric => FormatDockMetric(metric, localization)));
        }

        var windows = new List<string>(2);
        if (snapshot.PrimaryWindow is not null)
        {
            windows.Add(FormatDockWindow(snapshot.PrimaryWindow, isPrimary: true, localization));
        }
        if (snapshot.SecondaryWindow is not null)
        {
            windows.Add(FormatDockWindow(snapshot.SecondaryWindow, isPrimary: false, localization));
        }

        var subtitle = string.Join(", ", windows);
        var totalBalance = metrics.FirstOrDefault(metric =>
            string.Equals(metric.SemanticKey, "totalBalance", StringComparison.OrdinalIgnoreCase));
        if (totalBalance is not null)
        {
            subtitle += $", {FormatDockMetric(totalBalance, localization)}";
        }

        return estimatePrefix + subtitle;
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
        if (seconds % 60 != 0)
        {
            return fallback;
        }

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
        if (seconds % 60 != 0)
        {
            var key = fallback == "Основное" ? "details.primary" : "details.secondary";
            return localization.GetString(key, fallback);
        }

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

    private static UsageMetric[] GetPrioritizedMetrics(IReadOnlyList<UsageMetric> metrics)
        => metrics
            .Select((metric, index) => (metric, index))
            .OrderBy(item => GetMetricPriority(item.metric))
            .ThenBy(item => item.index)
            .Select(item => item.metric)
            .ToArray();

    private static int GetMetricPriority(UsageMetric metric)
        => metric.SemanticKey?.ToLowerInvariant() switch
        {
            "tokens5h" => 0,
            "tokens7d" => 1,
            "totalbalance" => 2,
            _ => 3,
        };

    private static string FormatDockMetric(UsageMetric metric, ILocalizationService? localization)
    {
        var name = localization is null ? metric.Name : GetMetricName(metric, localization);
        if (string.Equals(metric.SemanticKey, "totalBalance", StringComparison.OrdinalIgnoreCase)
            && localization is not null)
        {
            name = localization.GetString("metrics.totalBalance", "Total balance");
        }

        var culture = localization?.Culture ?? CultureInfo.InvariantCulture;
        return $"{TrimMetricValue(name)}: {TrimMetricValue(FormatMetric(metric, culture))}";
    }

    private static string FormatDockWindow(UsageWindow window, bool isPrimary, ILocalizationService? localization)
    {
        var fallback = isPrimary ? "Основное" : "Дополнительное";
        var label = localization is null
            ? GetLegacyWindowShortLabel(window, fallback)
            : GetWindowShortLabel(window, fallback, localization);
        return $"{label}\\{FormatDockPercent(window)}";
    }

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
