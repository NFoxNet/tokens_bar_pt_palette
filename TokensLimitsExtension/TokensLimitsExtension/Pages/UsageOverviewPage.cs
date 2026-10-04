using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
    private readonly ICommand? _settingsCommand;
    private IListItem[] _items;
    private string[] _itemProviderIds = [];
    private string? _renderSignature;
    private int _disposed;

    public UsageOverviewPage(
        IReadOnlyList<UsageSnapshotCache> caches,
        IReadOnlyList<TokensLimitsPage> pages,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null,
        ILocalizationService? localization = null,
        UsageRefreshCoordinator? coordinator = null,
        ICommand? settingsCommand = null)
    {
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _localization = localization ?? InvariantLocalizationService.Instance;
        _coordinator = coordinator;
        _settingsCommand = settingsCommand;
        Id = "com.tokenslimits.overview";
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("overview.providers", "Enabled providers");
        PlaceholderText = Name;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _items = CreateLoadingItems();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Subscribe(_caches);
        RebuildItems();
    }

    public override IListItem[] GetItems()
    {
        if (Volatile.Read(ref _disposed) != 0) return [];
        return Volatile.Read(ref _items);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        UsageSnapshotCache[] caches;
        lock (_providerGate) caches = _caches.ToArray();
        foreach (var cache in caches)
        {
            if (_coordinator is not null) _ = _coordinator.RefreshProviderAsync(cache, force: true);
            else _ = cache.RefreshAsync(force: true, cancellationToken: cancellationToken);
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
        UsageSnapshotCache[] updatedCaches;
        lock (_providerGate) updatedCaches = _caches.ToArray();
        foreach (var cache in updatedCaches)
        {
            if (_coordinator is not null) _ = _coordinator.RefreshProviderAsync(cache);
            else _ = cache.RefreshAsync();
        }
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
        var entries = new List<(UsageSnapshotCache Cache, ListPage Page, string Title, string Subtitle, Details Details, IContextItem[] MoreCommands)>();
        for (var index = 0; index < caches.Count; index++)
        {
            var cache = caches[index];
            var state = cache.State;
            var subtitle = state.Snapshot is not null
                ? FormatSnapshotSubtitle(state)
                : GetStatusText(state);
            entries.Add((cache, pages[index], cache.Descriptor.DisplayName, subtitle, CreateDetails(state), CreateMoreCommands(cache, state)));
        }
        var signature = entries.Count == 0
            ? $"empty\u001e{_localization.GetString("overview.empty.title", "No providers enabled")}\u001e{_localization.GetString("overview.empty.subtitle", "Enable providers in the extension settings.")}"
            : string.Join('\u001f', entries.Select(entry =>
                $"{entry.Cache.Descriptor.Id}\u001e{RuntimeHelpers.GetHashCode(entry.Page)}\u001e{entry.Title}\u001e{entry.Subtitle}\u001e{entry.Details.Title}\u001e{entry.Details.Body}\u001e{string.Join('\u001d', entry.MoreCommands.Select(GetContextSignature))}"));
        if (string.Equals(signature, _renderSignature, StringComparison.Ordinal))
        {
            return;
        }

        if (entries.Count == _itemProviderIds.Length
            && entries.Select(entry => entry.Cache.Descriptor.Id).SequenceEqual(_itemProviderIds, StringComparer.OrdinalIgnoreCase)
            && _items.Length == entries.Count
            && _items.Zip(entries).All(pair => pair.First is ListItem item
                && ReferenceEquals(item.Command, pair.Second.Page)))
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var item = (ListItem)_items[index];
                item.Title = entries[index].Title;
                item.Subtitle = entries[index].Subtitle;
                item.Details = entries[index].Details;
                item.MoreCommands = entries[index].MoreCommands;
            }

            _renderSignature = signature;
            RaiseItemsChanged(entries.Count);
            return;
        }

        var items = entries
            .Select(entry => (IListItem)new ListItem(entry.Page)
            {
                Title = entry.Title,
                Subtitle = entry.Subtitle,
                Details = entry.Details,
                MoreCommands = entry.MoreCommands,
            })
            .ToList();
        if (items.Count == 0) items.Add(new ListItem(_settingsCommand ?? new NoOpCommand())
        {
            Title = _localization.GetString("overview.empty.title", "No providers enabled"),
            Subtitle = _localization.GetString("overview.empty.subtitle", "Enable providers in the extension settings."),
        });
        _itemProviderIds = entries.Select(entry => entry.Cache.Descriptor.Id).ToArray();
        _renderSignature = signature;
        Volatile.Write(ref _items, items.ToArray());
        RaiseItemsChanged(items.Count);
    }

    private IListItem[] CreateLoadingItems() => [new ListItem(new NoOpCommand())
    {
        Title = _localization.GetString("overview.providers", "Enabled providers"),
        Subtitle = _localization.GetString("overview.loading", "Loading…"),
    }];

    private string GetStatusText(UsageProviderState state)
    {
        if (state.IsRefreshing) return _localization.GetString("overview.loading", "Loading…");
        return state.ErrorKind == UsageProviderErrorKind.None
            ? _localization.GetString("overview.unavailable", "Data unavailable")
            : string.Concat(
                _localization.GetString("status.unavailable", "Limits unavailable"),
                " · ",
                GetErrorLabel(state.ErrorKind));
    }

    private string FormatSnapshotSubtitle(UsageProviderState state)
    {
        var value = UsageDisplayFormatter.FormatDockBandSubtitle(state.Snapshot!, _localization);
        var parts = new List<string> { value };
        if (state.Snapshot!.FetchedAt is { } fetchedAt)
        {
            parts.Add(_localization.Format("details.updatedAt", fetchedAt.ToLocalTime().ToString("g", _localization.Culture)));
        }

        var status = state.IsRefreshing
            ? _localization.GetString("status.refreshing", "Refreshing…")
            : string.Empty;
        if (state.IsStale)
        {
            status = string.Join(" · ", new[]
            {
                _localization.GetString("status.stale", "Stale"),
                status,
                GetErrorLabel(state.ErrorKind),
            }.Where(part => part.Length > 0));
        }
        if (status.Length > 0) parts.Add(status);
        return string.Join(" · ", parts);
    }

    private Details CreateDetails(UsageProviderState state)
    {
        var status = state.Snapshot is null
            ? GetStatusText(state)
            : state.IsStale
                ? string.Join(" · ", _localization.GetString("status.stale", "Stale"), GetErrorLabel(state.ErrorKind))
                : _localization.GetString("status.ready", "Available");
        var lines = new List<string>
        {
            $"**{_localization.GetString("overview.status", "Status")}:** {EscapeDetailsText(status)}",
        };
        if (state.Snapshot?.Plan is { Length: > 0 } plan)
        {
            lines.Add($"**{_localization.GetString("details.plan", "Plan")}:** {EscapeDetailsText(plan)}");
        }

        if (state.Snapshot?.Source is { Length: > 0 } source)
        {
            lines.Add($"**{_localization.GetString("details.source", "Source")}:** {EscapeDetailsText(FormatSafeSource(source))}");
        }

        if (state.Snapshot?.FetchedAt is { } fetchedAt)
        {
            lines.Add($"**{_localization.GetString("details.lastUpdated", "Last updated")}:** {fetchedAt.ToLocalTime().ToString("g", _localization.Culture)}");
        }

        if (state.IsRefreshing)
        {
            lines.Add(_localization.GetString("status.refreshingSubtitle", "Fetching the latest provider data."));
        }

        return new Details
        {
            Title = _localization.GetString("overview.detailsTitle", "Provider details"),
            Body = string.Join(Environment.NewLine + Environment.NewLine, lines),
        };
    }

    private static string EscapeDetailsText(string value)
        => string.Concat(value.Select(character => character switch
        {
            '\\' => "\\\\",
            '`' => "\\`",
            '*' => "\\*",
            '_' => "\\_",
            '[' => "\\[",
            ']' => "\\]",
            '(' => "\\(",
            ')' => "\\)",
            '<' => "&lt;",
            '>' => "&gt;",
            '|' => "\\|",
            '\r' or '\n' => " ",
            _ when char.IsControl(character) => string.Empty,
            _ => character.ToString(),
        }));

    private static string FormatSafeSource(string source)
    {
        var sources = source
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Uri.TryCreate(part, UriKind.Absolute, out var uri)
                ? uri.IsFile
                    ? Path.GetFileName(uri.LocalPath)
                    : new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.GetLeftPart(UriPartial.Path)
                : part)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var formatted = string.Join(", ", sources);
        return formatted.Length <= 256 ? formatted : string.Concat(formatted.AsSpan(0, 253), "…");
    }

    private IContextItem[] CreateMoreCommands(UsageSnapshotCache cache, UsageProviderState state)
    {
        var commands = new List<IContextItem>
        {
            new CommandContextItem(new AnonymousCommand(() => _ = RefreshProviderAsync(cache)))
            {
                Title = _localization.GetString("action.refresh", "Refresh"),
            },
        };
        if (Uri.TryCreate(cache.Descriptor.DashboardUrl, UriKind.Absolute, out var dashboardUrl)
            && dashboardUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            commands.Add(new CommandContextItem(new OpenUrlCommand(dashboardUrl.AbsoluteUri))
            {
                Title = _localization.GetString("action.openDashboard", "Open provider dashboard"),
            });
        }

        commands.Add(new CommandContextItem(new CopyTextCommand(BuildSafeDiagnostics(cache, state)))
        {
            Title = _localization.GetString("action.copyDiagnostics", "Copy safe diagnostics"),
        });
        return commands.ToArray();
    }

    private async Task RefreshProviderAsync(UsageSnapshotCache cache)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_coordinator is not null) await _coordinator.RefreshProviderAsync(cache, force: true).ConfigureAwait(false);
        else await cache.RefreshAsync(force: true).ConfigureAwait(false);
    }

    private static string BuildSafeDiagnostics(UsageSnapshotCache cache, UsageProviderState state)
        => string.Join(Environment.NewLine,
            $"provider={cache.Descriptor.Id}",
            $"error={state.ErrorKind}",
            $"stale={state.IsStale}",
            $"retry_after_until={state.RetryAfterUntil?.ToUniversalTime().ToString("O") ?? "none"}");

    private string GetErrorLabel(UsageProviderErrorKind errorKind)
        => errorKind switch
        {
            UsageProviderErrorKind.MissingConfiguration => _localization.GetString("status.kind.configuration", "Configuration required"),
            UsageProviderErrorKind.Authentication => _localization.GetString("status.kind.authentication", "Authentication required"),
            UsageProviderErrorKind.RateLimited => _localization.GetString("status.kind.rateLimited", "Rate limited"),
            UsageProviderErrorKind.Timeout => _localization.GetString("status.kind.timeout", "Request timed out"),
            UsageProviderErrorKind.Network => _localization.GetString("status.kind.network", "Network error"),
            UsageProviderErrorKind.UnsupportedResponse => _localization.GetString("status.kind.unsupported", "Unsupported response"),
            _ => _localization.GetString("status.kind.unknown", "Unknown error"),
        };

    private static string GetCommandSignature(ICommand? command)
        => command is CopyTextCommand copy
            ? $"copy:{copy.Text}"
            : command?.GetType().FullName ?? string.Empty;

    private static string GetContextSignature(IContextItem item)
        => item is CommandContextItem context
            ? $"{context.Title}\u001e{context.Subtitle}\u001e{GetCommandSignature(context.Command)}"
            : item.GetType().FullName ?? string.Empty;

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
