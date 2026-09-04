using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>Overview of enabled providers backed solely by shared cache state.</summary>
public sealed partial class UsageOverviewPage : ListPage, IDisposable
{
    private IReadOnlyList<UsageSnapshotCache> _caches;
    private IReadOnlyList<TokensLimitsPage> _pages;
    private readonly object _providerGate = new();
    private readonly ILocalizationService _localization;
    private readonly UsageRefreshCoordinator? _coordinator;
    private IListItem[] _items;
    private int _disposed;

    public UsageOverviewPage(
        IReadOnlyList<UsageSnapshotCache> caches,
        IReadOnlyList<TokensLimitsPage> pages,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null,
        ILocalizationService? localization = null,
        UsageRefreshCoordinator? coordinator = null)
    {
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _localization = localization ?? InvariantLocalizationService.Instance;
        _coordinator = coordinator;
        Id = "com.tokenslimits.overview";
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("overview.providers", "Enabled providers");
        PlaceholderText = Name;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _items = CreateLoadingItems();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Subscribe(_caches);
    }

    public override IListItem[] GetItems()
    {
        if (Volatile.Read(ref _disposed) != 0) return [];
        _ = RefreshAsync();
        return Volatile.Read(ref _items);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        UsageSnapshotCache[] caches;
        lock (_providerGate) caches = _caches.ToArray();
        foreach (var cache in caches)
        {
            if (_coordinator is not null) _ = _coordinator.RefreshProviderAsync(cache);
            else _ = cache.RefreshAsync(cancellationToken: cancellationToken);
        }
        RebuildItems();
        return Task.CompletedTask;
    }

    public void UpdateProviders(IReadOnlyList<UsageSnapshotCache> caches, IReadOnlyList<TokensLimitsPage> pages)
    {
        ArgumentNullException.ThrowIfNull(caches);
        ArgumentNullException.ThrowIfNull(pages);
        if (caches.Count != pages.Count) throw new ArgumentException("Provider caches and pages must have the same length.");
        UsageSnapshotCache[] previous;
        lock (_providerGate)
        {
            previous = _caches.ToArray();
            _caches = caches;
            _pages = pages;
        }
        Unsubscribe(previous);
        Subscribe(caches);
        RebuildItems();
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        Unsubscribe(_caches);
        GC.SuppressFinalize(this);
    }

    private void RebuildItems()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        IReadOnlyList<UsageSnapshotCache> caches;
        IReadOnlyList<TokensLimitsPage> pages;
        lock (_providerGate) { caches = _caches; pages = _pages; }
        var items = new List<IListItem>();
        for (var index = 0; index < caches.Count; index++)
        {
            var state = caches[index].State;
            var subtitle = state.Snapshot is not null
                ? FormatSnapshotSubtitle(state)
                : GetStatusText(state);
            items.Add(new ListItem(pages[index]) { Title = caches[index].Descriptor.DisplayName, Subtitle = subtitle });
        }
        if (items.Count == 0) items.Add(new ListItem(new NoOpCommand())
        {
            Title = _localization.GetString("overview.empty.title", "No providers enabled"),
            Subtitle = _localization.GetString("overview.empty.subtitle", "Enable providers in the extension settings."),
        });
        Volatile.Write(ref _items, items.ToArray());
        RaiseItemsChanged(items.Count);
    }

    private IListItem[] CreateLoadingItems() => [new ListItem(new NoOpCommand())
    {
        Title = _localization.GetString("overview.providers", "Enabled providers"),
        Subtitle = _localization.GetString("overview.loading", "Loading…"),
    }];

    private string GetStatusText(UsageProviderState state) => state.IsRefreshing
        ? _localization.GetString("overview.loading", "Loading…")
        : state.ErrorKind == UsageProviderErrorKind.None
            ? _localization.GetString("overview.unavailable", "Data unavailable")
            : _localization.GetString("status.unavailable", "Limits unavailable");

    private string FormatSnapshotSubtitle(UsageProviderState state)
    {
        var value = UsageDisplayFormatter.FormatDockBandSubtitle(state.Snapshot!, _localization);
        return state.IsStale
            ? string.Concat(value, " · ", _localization.GetString("status.stale", "Stale"))
            : value;
    }

    private void Subscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged += CacheOnStateChanged;
    }
    private void Unsubscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged -= CacheOnStateChanged;
    }
    private void CacheOnStateChanged(object? sender, EventArgs e) => RebuildItems();
    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("overview.providers", "Enabled providers");
        PlaceholderText = Name;
        RebuildItems();
    }
}
