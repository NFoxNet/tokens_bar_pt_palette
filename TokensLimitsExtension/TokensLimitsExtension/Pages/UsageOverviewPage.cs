using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Core.Providers;

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
    private IListItem[] _items = [];
    private UsageSnapshotCache[] _itemCaches = [];
    private UsageProviderState[] _renderedStates = [];
    private long _renderRevision;
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
        lock (_providerGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Unsubscribe(_caches);
            _caches = caches;
            _pages = pages;
            Subscribe(caches);
            _renderRevision++;
        }
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
        lock (_providerGate) Unsubscribe(_caches);
        GC.SuppressFinalize(this);
    }

    private void RebuildItems(bool languageChanged = false)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            IReadOnlyList<UsageSnapshotCache> caches;
            IReadOnlyList<TokensLimitsPage> pages;
            long revision;
            lock (_providerGate) { caches = _caches; pages = _pages; revision = _renderRevision; }
            // Cache events hold their state lock. Never read another cache under the page lock.
            var states = caches.Select(cache => cache.State).ToArray();
            var changed = false;
            lock (_providerGate)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (revision != _renderRevision) continue;
                var sameComposition = caches.Count == _itemCaches.Length && _items.Length == caches.Count;
                for (var index = 0; sameComposition && index < caches.Count; index++)
                {
                    sameComposition = ReferenceEquals(caches[index], _itemCaches[index])
                        && ReferenceEquals(_items[index].Command, pages[index]);
                }

                if (caches.Count == 0)
                {
                    var title = _localization.GetString("overview.empty.title", "No providers enabled");
                    var subtitle = _localization.GetString("overview.empty.subtitle", "Enable providers in the extension settings.");
                    if (_itemCaches.Length != 0 || _items.Length != 1)
                    {
                        Volatile.Write(ref _items, [new ListItem(_settingsCommand ?? new NoOpCommand()) { Title = title, Subtitle = subtitle }]);
                        _itemCaches = [];
                        _renderedStates = [];
                        changed = true;
                    }
                    else
                    {
                        var item = (ListItem)_items[0];
                        changed = item.Title != title || item.Subtitle != subtitle;
                        if (changed) { item.Title = title; item.Subtitle = subtitle; }
                    }
                }
                else if (sameComposition)
                {
                    for (var index = 0; index < caches.Count; index++)
                    {
                        changed |= UpdateItem(index, states[index], languageChanged);
                    }
                }
                else
                {
                    var items = new IListItem[caches.Count];
                    for (var index = 0; index < caches.Count; index++)
                    {
                        var state = states[index];
                        items[index] = new ListItem(pages[index])
                        {
                            Title = caches[index].Descriptor.DisplayName,
                            Subtitle = state.Snapshot is null ? GetStatusText(state) : FormatSnapshotSubtitle(state),
                            Details = new Details
                            {
                                Title = _localization.GetString("overview.detailsTitle", "Provider details"),
                                Body = CreateDetailsBody(state),
                            },
                            MoreCommands = CreateMoreCommands(caches[index], state),
                        };
                    }
                    _itemCaches = caches.ToArray();
                    _renderedStates = states;
                    Volatile.Write(ref _items, items);
                    changed = true;
                }
                _renderRevision++;
            }
            if (changed) RaiseItemsChanged(_items.Length);
            return;
        }
    }

    private bool UpdateItem(int index, UsageProviderState state, bool languageChanged = false)
    {
        var previous = _renderedStates[index];
        _renderedStates[index] = state;
        if (!languageChanged && ReferenceEquals(previous.Snapshot, state.Snapshot)
            && previous.IsRefreshing == state.IsRefreshing && previous.ErrorKind == state.ErrorKind
            && previous.RetryAfterUntil == state.RetryAfterUntil) return false;

        var item = (ListItem)_items[index];
        var subtitle = state.Snapshot is null ? GetStatusText(state) : FormatSnapshotSubtitle(state);
        var details = (Details)item.Details!;
        var detailsTitle = _localization.GetString("overview.detailsTitle", "Provider details");
        var detailsBody = CreateDetailsBody(state);
        var changed = item.Subtitle != subtitle || details.Title != detailsTitle || details.Body != detailsBody;
        if (item.Subtitle != subtitle) item.Subtitle = subtitle;
        if (details.Title != detailsTitle) details.Title = detailsTitle;
        if (details.Body != detailsBody) details.Body = detailsBody;
        var commands = item.MoreCommands!;
        for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
        {
            var context = (CommandContextItem)commands[commandIndex];
            var title = context.Command switch
            {
                CopyTextCommand => _localization.GetString("action.copyDiagnostics", "Copy safe diagnostics"),
                OpenUrlCommand => _localization.GetString("action.openDashboard", "Open provider dashboard"),
                _ when commandIndex == 0 => _localization.GetString("action.refresh", "Refresh"),
                _ => _localization.GetString("action.validateConnection", "Validate connection"),
            };
            if (context.Title != title) { context.Title = title; changed = true; }
            if (context.Command is CopyTextCommand copy)
            {
                var text = BuildSafeDiagnostics(_itemCaches[index], state);
                if (copy.Text != text) { copy.Text = text; changed = true; }
            }
            else if (commandIndex > 0 && context.Command is AnonymousCommand)
            {
                var commandSubtitle = _localization.GetString("action.validateConnectionSubtitle", "Send one deployment request to check connection; may consume quota.");
                if (context.Subtitle != commandSubtitle) { context.Subtitle = commandSubtitle; changed = true; }
            }
        }
        return changed;
    }

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

    private string CreateDetailsBody(UsageProviderState state)
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

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
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
            .Select(FormatSafeSourcePart)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var formatted = string.Join(", ", sources);
        return formatted.Length <= 256 ? formatted : string.Concat(formatted.AsSpan(0, 253), "…");
    }

    private static string FormatSafeSourcePart(string part)
    {
        if (Uri.TryCreate(part, UriKind.Absolute, out var uri))
        {
            return uri.IsFile
                ? Path.GetFileName(uri.LocalPath)
                : new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.GetLeftPart(UriPartial.Path);
        }

        var suffixIndex = part.IndexOfAny(['?', '#']);
        return suffixIndex >= 0 ? part[..suffixIndex] : part;
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

        if (cache.SupportsConnectionValidation)
        {
            commands.Add(new CommandContextItem(new AnonymousCommand(() => _ = ValidateConnectionAsync(cache)))
            {
                Title = _localization.GetString("action.validateConnection", "Validate connection"),
                Subtitle = _localization.GetString("action.validateConnectionSubtitle", "Send one deployment request to check connection; may consume quota."),
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

    private async Task ValidateConnectionAsync(UsageSnapshotCache cache)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (_coordinator is not null) await _coordinator.ValidateProviderConnectionAsync(cache).ConfigureAwait(false);
            else await cache.ValidateConnectionAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is UsageProviderConfigurationException
            or UsageProviderRequestException
            or TimeoutException
            or System.Net.Http.HttpRequestException)
        {
            Debug.WriteLine($"[TokensLimits] Connection validation failed ({exception.GetType().Name}); provider state was updated.");
        }
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

    private void Subscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged += CacheOnStateChanged;
    }
    private void Unsubscribe(IEnumerable<UsageSnapshotCache> caches)
    {
        foreach (var cache in caches) cache.StateChanged -= CacheOnStateChanged;
    }
    private void CacheOnStateChanged(object? sender, EventArgs e)
    {
        if (sender is not UsageSnapshotCache cache || Volatile.Read(ref _disposed) != 0) return;
        var state = cache.State;
        var changed = false;
        lock (_providerGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            for (var index = 0; index < _caches.Count; index++)
            {
                if (!ReferenceEquals(_caches[index], cache)) continue;
                _renderRevision++;
                if (index < _itemCaches.Length && ReferenceEquals(_itemCaches[index], cache)
                    && ReferenceEquals(_items[index].Command, _pages[index]))
                {
                    changed = UpdateItem(index, state);
                }
                break;
            }
        }
        if (changed) RaiseItemsChanged(_items.Length);
    }
    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        lock (_providerGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Title = _localization.GetString("app.title", "Tokens Limits");
            Name = _localization.GetString("overview.providers", "Enabled providers");
            PlaceholderText = Name;
            _renderRevision++;
        }
        RebuildItems(languageChanged: true);
    }
}
