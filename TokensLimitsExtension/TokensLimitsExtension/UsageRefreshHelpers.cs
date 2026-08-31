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

    public static double GetRefreshIntervalMilliseconds(IUsageRefreshSettings? settings)
        => Math.Max(1000, (settings?.RefreshInterval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds);

    public static void ApplySettingsChanged(
        Timer refreshTimer,
        IUsageRefreshSettings? refreshSettings,
        Func<Task> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(refreshTimer);
        ArgumentNullException.ThrowIfNull(refreshAsync);

        try
        {
            refreshTimer.Interval = GetRefreshIntervalMilliseconds(refreshSettings);
        }
        catch (ObjectDisposedException)
        {
            // A settings event can already have captured a surface handler while
            // the provider is concurrently disposing that surface.
            return;
        }

        _ = refreshAsync();
    }
}
