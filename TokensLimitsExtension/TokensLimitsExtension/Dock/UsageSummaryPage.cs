using System;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>A native flyout projected from visible provider snapshots; it never starts refreshes.</summary>
public sealed partial class UsageSummaryPage : ContentPage, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly Func<string> _metricSelection;
    private UsageSnapshotCache[] _caches = [];
    private int _disposed;

    public UsageSummaryPage(ILocalizationService localization, Func<string> metricSelection)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _metricSelection = metricSelection ?? throw new ArgumentNullException(nameof(metricSelection));
        Id = TokensLimitsDockBandPage.StableId;
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("dock.summary.title", "Usage summary");
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public override IContent[] GetContent()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return [];
        }

        var content = Volatile.Read(ref _caches)
            .Select(cache => (Cache: cache, State: cache.State))
            .Select(item => FormatProvider(item.Cache, item.State))
            .ToArray();
        var body = content.Length == 0
            ? _localization.GetString("dock.summary.empty", "No providers are visible in the Dock.")
            : string.Join(Environment.NewLine + Environment.NewLine, content);
        return [new MarkdownContent(body)];
    }

    public void UpdateProviders(UsageSnapshotCache[] caches)
    {
        ArgumentNullException.ThrowIfNull(caches);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var current = Volatile.Read(ref _caches);
        if (current.SequenceEqual(caches))
        {
            return;
        }

        foreach (var cache in current)
        {
            cache.StateChanged -= OnCacheChanged;
        }

        var updated = caches.ToArray();
        Volatile.Write(ref _caches, updated);
        foreach (var cache in updated)
        {
            cache.StateChanged += OnCacheChanged;
        }

        RaiseItemsChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var cache in Volatile.Read(ref _caches))
        {
            cache.StateChanged -= OnCacheChanged;
        }

        Volatile.Write(ref _caches, []);
        _localization.LanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    private string FormatProvider(UsageSnapshotCache cache, UsageProviderState state)
    {
        var name = UsageOverviewPage.EscapeDetailsText(cache.Descriptor.DisplayName);
        var snapshot = state.Snapshot;
        if (snapshot is null)
        {
            return $"**{name}**  \n{_localization.GetString("status.unavailable", "Limits unavailable")}";
        }

        var value = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, _localization, _metricSelection());
        var fetchedAt = state.LastSuccessfulRefreshAt ?? snapshot.FetchedAt;
        var updated = fetchedAt?.ToLocalTime().ToString("g", _localization.Culture)
            ?? _localization.GetString("dock.summary.updatedUnknown", "Unknown");
        var source = snapshot.Source is null
            ? _localization.GetString("dock.summary.sourceUnknown", "Unknown")
            : UsageOverviewPage.EscapeDetailsText(UsageOverviewPage.FormatSafeSource(snapshot.Source));
        var stale = state.IsStale
            ? $" · {_localization.GetString("dock.summary.stale", "Stale data")}" : string.Empty;
        return $"**{name}**  \n{UsageOverviewPage.EscapeDetailsText(value)}  \n{_localization.GetString("dock.summary.fetchedAt", "Updated")}: {UsageOverviewPage.EscapeDetailsText(updated)} · {_localization.GetString("dock.summary.source", "Source")}: {source}{stale}";
    }

    private void OnCacheChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            RaiseItemsChanged();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Title = _localization.GetString("app.title", "Tokens Limits");
            Name = _localization.GetString("dock.summary.title", "Usage summary");
            RaiseItemsChanged();
        }
    }
}
