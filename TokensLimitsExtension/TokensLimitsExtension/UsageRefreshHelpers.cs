using System;
using System.Threading.Tasks;
using System.Timers;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

internal static class UsageRefreshHelpers
{
    public static bool TryGetCachedSnapshot(
        this IUsageProvider provider,
        out UsageSnapshot snapshot)
    {
        if (provider is IRefreshableUsageProvider refreshableProvider
            && refreshableProvider.TryGetSnapshot(out snapshot))
        {
            return true;
        }

        snapshot = null!;
        return false;
    }

    public static void InvalidateIfSupported(this IUsageProvider provider)
    {
        if (provider is IRefreshableUsageProvider refreshableProvider)
        {
            refreshableProvider.Invalidate();
        }
    }

    public static double GetRefreshIntervalMilliseconds(IUsageRefreshSettings? settings)
        => Math.Max(1000, (settings?.RefreshInterval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds);

    public static void ApplySettingsChanged(
        IUsageProvider provider,
        Timer refreshTimer,
        IUsageRefreshSettings? refreshSettings,
        Func<Task> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(refreshTimer);
        ArgumentNullException.ThrowIfNull(refreshAsync);

        refreshTimer.Interval = GetRefreshIntervalMilliseconds(refreshSettings);
        provider.InvalidateIfSupported();
        _ = refreshAsync();
    }
}
