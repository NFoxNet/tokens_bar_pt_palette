using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TokensLimitsExtension;

public sealed partial class UsageWrappedDockItem : WrappedDockItem
{
    private readonly string _displayTitle;
    private string _title;

    public UsageWrappedDockItem(
        IListItem[] items,
        string id,
        string displayTitle,
        string dockSubtitle)
        : base(items, id, displayTitle)
    {
        _displayTitle = displayTitle ?? throw new ArgumentNullException(nameof(displayTitle));
        _title = CreateTitle(_displayTitle, dockSubtitle);
        Subtitle = dockSubtitle;
    }

    public override string Title => _title;

    public void UpdateDockSubtitle(string dockSubtitle)
    {
        var title = CreateTitle(_displayTitle, dockSubtitle);
        if (string.Equals(_title, title, StringComparison.Ordinal))
        {
            return;
        }

        _title = title;
        Subtitle = dockSubtitle;
        OnPropertyChanged(nameof(Title));
    }

    private static string CreateTitle(string displayTitle, string dockSubtitle)
        => string.IsNullOrWhiteSpace(dockSubtitle)
            ? displayTitle
            : $"{displayTitle} {dockSubtitle}";
}
