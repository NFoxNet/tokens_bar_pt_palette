using System;
using System.Collections.Generic;
using System.Linq;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Localization;

/// <summary>
/// Stable contract for complete language packs. Provider names are brands and
/// deliberately stay outside this catalog; every other settings label is keyed
/// by the stable provider setting key.
/// </summary>
public static class LocalizationKeyCatalog
{
    private static readonly string[] CommonKeys =
    [
        "app.title",
        "overview.providers",
        "overview.loading",
        "overview.empty.title",
        "overview.empty.subtitle",
        "overview.unavailable",
        "dock.show",
        "details.show",
        "details.limits",
        "details.loading",
        "details.plan",
        "details.primary",
        "details.secondary",
        "metrics.totalBalance",
        "metrics.currency",
        "status.estimate",
        "status.unavailable",
        "status.stale",
        "status.remaining",
        "status.reset",
        "status.resetPassed",
        "settings.language",
        "settings.languageDescription",
        "settings.auto",
        "settings.refreshInterval",
        "settings.refreshIntervalDescription",
        "settings.refreshIntervalError",
        "settings.enableProvider",
        "settings.providerSource",
        "settings.secretPlaceholder",
        "settings.field.environmentVariable",
        "time.hours",
        "time.days",
        "time.minutes",
        "window.weekly",
    ];

    public static IReadOnlyList<string> RequiredKeys { get; } = CommonKeys
        .Concat(UsageProviderDescriptorRegistry.All
            .SelectMany(descriptor => descriptor.Settings)
            .SelectMany(setting => new[]
            {
                $"settings.field.{setting.Key}.label",
                $"settings.field.{setting.Key}.description",
            }))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(key => key, StringComparer.Ordinal)
        .ToArray();
}
