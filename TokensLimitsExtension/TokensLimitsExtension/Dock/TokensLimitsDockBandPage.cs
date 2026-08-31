using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

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
    private int _disposed;

    public TokensLimitsDockBandPage()
    {
        Id = StableId;
        Title = "Tokens Limits";
        Name = "Show enabled provider limits in Dock";
        PlaceholderText = "Enabled providers";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
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

        var updatedItems = items.Cast<IListItem>().ToArray();
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
        GC.SuppressFinalize(this);
    }
}
