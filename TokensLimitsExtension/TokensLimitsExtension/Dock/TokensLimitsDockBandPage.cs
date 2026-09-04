using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>
/// Stable Dock band that owns the current list of enabled provider widgets.
/// Command Palette persists a Dock pin by command ID, so the band itself must
/// outlive provider enablement changes while its items may be replaced.
/// </summary>
public sealed partial class TokensLimitsDockBandPage : ListPage, IDisposable
{
    // Keep the original Codex band ID so existing Dock pins migrate in place.
    public const string StableId = "com.tokenslimits.provider.codex.band";

    private IListItem[] _items = [];
    private UsageDockBandItem[] _observedItems = [];
    private readonly ILocalizationService _localization;
    private int _disposed;

    public TokensLimitsDockBandPage(ILocalizationService? localization = null)
    {
        _localization = localization ?? InvariantLocalizationService.Instance;
        Id = StableId;
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("dock.show", "Show enabled provider limits in Dock");
        PlaceholderText = _localization.GetString("overview.providers", "Enabled providers");
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
    }

    public override IListItem[] GetItems()
        => Volatile.Read(ref _disposed) != 0 ? [] : Volatile.Read(ref _items);

    public void UpdateItems(IReadOnlyList<UsageDockBandItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var previousItems = _observedItems;
        var updatedObservedItems = items.ToArray();
        foreach (var item in previousItems)
        {
            item.PropChanged -= DockItemOnPropChanged;
        }

        foreach (var item in updatedObservedItems)
        {
            item.PropChanged += DockItemOnPropChanged;
        }

        var updatedItems = updatedObservedItems.Cast<IListItem>().ToArray();
        _observedItems = updatedObservedItems;
        Volatile.Write(ref _items, updatedItems);
        RaiseItemsChanged(updatedItems.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _items, []);
        foreach (var item in _observedItems)
        {
            item.PropChanged -= DockItemOnPropChanged;
        }
        _observedItems = [];
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        GC.SuppressFinalize(this);
    }

    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("dock.show", "Show enabled provider limits in Dock");
        PlaceholderText = _localization.GetString("overview.providers", "Enabled providers");
        RaiseItemsChanged(Volatile.Read(ref _items).Length);
    }

    private void DockItemOnPropChanged(object sender, IPropChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0
            && (args.PropertyName == nameof(UsageDockBandItem.Title)
                || args.PropertyName == nameof(UsageDockBandItem.Subtitle)))
        {
            // Dock does not always repaint a nested ListItem property change.
            // Re-publishing its stable list makes a shared-cache update visible.
            RaiseItemsChanged(Volatile.Read(ref _items).Length);
        }
    }
}
