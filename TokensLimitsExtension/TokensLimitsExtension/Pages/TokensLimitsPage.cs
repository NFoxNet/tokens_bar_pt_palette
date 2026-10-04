using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>Provider details rendered from the same state source as the Dock.</summary>
public sealed partial class TokensLimitsPage : ListPage, IDisposable
{
    private readonly IUsageProvider _usageProvider;
    private readonly IUsageProviderStateSource? _stateSource;
    private readonly UsageRefreshCoordinator? _coordinator;
    private readonly Action<string> _logger;
    private readonly ILocalizationService _localization;
    private IListItem[] _items;
    private UsageProviderState? _latestState;
    private bool _itemsMaterialized;
    private string? _renderSignature;
    private readonly object _lifecycleGate = new();
    private int _isActive = 1;
    private int _disposed;

    public TokensLimitsPage(CodexUsageService usageService, Action<string>? logger = null, IUsageRefreshSettings? refreshSettings = null)
        : this((ICodexUsageProvider)usageService, logger, refreshSettings) { }
    public TokensLimitsPage(ICodexUsageProvider usageService, Action<string>? logger = null, IUsageRefreshSettings? refreshSettings = null)
        : this(new CodexUsageProviderAdapter(usageService), logger, refreshSettings) { }

    public TokensLimitsPage(IUsageProvider usageProvider, Action<string>? logger = null, IUsageRefreshSettings? refreshSettings = null, string? idSuffix = null, ILocalizationService? localization = null, UsageRefreshCoordinator? coordinator = null)
    {
        _usageProvider = usageProvider ?? throw new ArgumentNullException(nameof(usageProvider));
        _stateSource = usageProvider as IUsageProviderStateSource;
        _coordinator = coordinator;
        _logger = logger ?? LogMessage;
        _localization = localization ?? InvariantLocalizationService.Instance;
        Icon = ProviderIconCatalog.For(_usageProvider.Descriptor.Id);
        var baseId = _usageProvider.Descriptor.Id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "com.tokenslimits.codex.limits" : $"com.tokenslimits.provider.{_usageProvider.Descriptor.Id}.limits";
        Id = string.IsNullOrWhiteSpace(idSuffix) ? baseId : $"{baseId}.{idSuffix}";
        Title = $"{_usageProvider.Descriptor.DisplayName} {_localization.GetString("details.limits", "Limits")}";
        Name = _localization.Format("details.show", _usageProvider.Descriptor.DisplayName);
        PlaceholderText = Title;
        ShowDetails = true;
        _items = [];
        _latestState = _stateSource?.State;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (_stateSource is not null) _stateSource.StateChanged += StateSourceOnStateChanged;
    }

    public override IListItem[] GetItems()
    {
        if (IsDisposed || !IsActive) return [];
        if (!Volatile.Read(ref _itemsMaterialized))
        {
            lock (_lifecycleGate)
            {
                if (IsDisposed || !IsActive) return [];
                if (!_itemsMaterialized)
                {
                    // First access projects cached state only; no cache locks, I/O or notifications.
                    if (_latestState is { } state) RenderState(state, notify: false);
                    if (_items.Length == 0) SetItems(CreateLoadingItems(), notify: false);
                    Volatile.Write(ref _itemsMaterialized, true);
                }
            }
        }
        return Volatile.Read(ref _items);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || !IsActive) return;
        if (_stateSource is not null)
        {
            if (_coordinator is not null) await _coordinator.RefreshProviderAsync(_stateSource, force: true).ConfigureAwait(false);
            else await _stateSource.RefreshAsync(force: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            ApplyState(_stateSource.State);
            return;
        }
        try { SetItems(CreateItems(await _usageProvider.GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false)), true); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger($"[TokensLimits] ERROR: {ex.Message}"); SetItems(CreateUnavailableItems(), true); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _isActive, 0);
            if (_stateSource is not null) _stateSource.StateChanged -= StateSourceOnStateChanged;
            _localization.LanguageChanged -= LocalizationOnLanguageChanged;
            _latestState = null;
            _renderSignature = null;
            Volatile.Write(ref _items, []);
            Volatile.Write(ref _itemsMaterialized, false);
        }
        GC.SuppressFinalize(this);
    }
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    internal bool IsActive => Volatile.Read(ref _isActive) != 0;
    internal void SetActive(bool active)
    {
        if (IsDisposed) return;
        var changed = false;
        lock (_lifecycleGate)
        {
            if (IsDisposed) return;
            var wasActive = Interlocked.Exchange(ref _isActive, active ? 1 : 0) != 0;
            changed = wasActive != active;
            if (changed)
            {
                if (active)
                {
                    if (_stateSource is not null) _stateSource.StateChanged += StateSourceOnStateChanged;
                    _localization.LanguageChanged += LocalizationOnLanguageChanged;
                }
                else
                {
                    if (_stateSource is not null) _stateSource.StateChanged -= StateSourceOnStateChanged;
                    _localization.LanguageChanged -= LocalizationOnLanguageChanged;
                    _renderSignature = null;
                    _latestState = null;
                    Volatile.Write(ref _items, []);
                    Volatile.Write(ref _itemsMaterialized, false);
                }
            }
        }

        if (!active)
        {
            if (changed && !IsDisposed) RaiseItemsChanged(0);
            return;
        }

        LocalizationOnLanguageChanged(this, EventArgs.Empty);
        SynchronizeState();
    }

    internal void SynchronizeState()
    {
        if (!IsDisposed && IsActive && _stateSource is not null)
        {
            ApplyState(_stateSource.State);
        }
    }

    private void StateSourceOnStateChanged(object? sender, EventArgs e) => ApplyState(_stateSource!.State);
    private void ApplyState(UsageProviderState state)
    {
        bool materialized;
        lock (_lifecycleGate)
        {
            if (IsDisposed || !IsActive) return;
            _latestState = state;
            materialized = _itemsMaterialized;
        }
        if (materialized) RenderState(state, notify: true);
        else if (!IsDisposed && IsActive) RaiseItemsChanged();
    }
    private void RenderState(UsageProviderState state, bool notify)
    {
        if (state.Snapshot is { } snapshot)
        {
            var items = new List<IListItem>(CreateItems(snapshot, state));
            if (state.IsStale)
            {
                items.Insert(0, new ListItem(new NoOpCommand())
                {
                    Title = _localization.GetString("status.stale", "Stale"),
                    Subtitle = state.IsRefreshing
                        ? string.Concat(GetStatusSubtitle(state), " · ", _localization.GetString("status.refreshing", "Refreshing…"))
                        : GetStatusSubtitle(state),
                });
            }
            SetItems([.. items], notify);
        }
        else if (!state.IsRefreshing) SetItems(CreateUnavailableItems(state), notify);
    }
    private void SetItems(IListItem[] items, bool notify)
    {
        if (IsDisposed || !IsActive) return;
        var signature = string.Join('\u001f', items.Select(item => item is ListItem listItem
            ? $"{listItem.Title}\u001e{listItem.Subtitle}\u001e{listItem.Details?.Title}\u001e{listItem.Details?.Body}\u001e{GetCommandSignature(listItem.Command)}"
            : $"{item.Title}\u001e{item.Subtitle}"));
        if (string.Equals(signature, _renderSignature, StringComparison.Ordinal))
        {
            return;
        }

        var currentItems = Volatile.Read(ref _items);
        if (currentItems.Length == items.Length
            && currentItems.All(item => item is ListItem)
            && items.All(item => item is ListItem))
        {
            for (var index = 0; index < items.Length; index++)
            {
                var current = (ListItem)currentItems[index];
                var updated = (ListItem)items[index];
                current.Title = updated.Title;
                current.Subtitle = updated.Subtitle;
                current.Details = updated.Details;
                if (current.Command is CopyTextCommand currentCopy
                    && updated.Command is CopyTextCommand updatedCopy)
                {
                    currentCopy.Text = updatedCopy.Text;
                }
            }

            _renderSignature = signature;
            if (notify && !IsDisposed && IsActive) RaiseItemsChanged(items.Length);
            return;
        }

        _renderSignature = signature;
        Volatile.Write(ref _items, items);
        Volatile.Write(ref _itemsMaterialized, true);
        if (notify && !IsDisposed && IsActive) RaiseItemsChanged(items.Length);
    }
    private IListItem[] CreateLoadingItems() => [new ListItem(new NoOpCommand()) { Title = _localization.GetString("details.limits", "Limits"), Subtitle = _localization.GetString("details.loading", "Loading…") }];
    private IListItem[] CreateUnavailableItems(UsageProviderState? state = null)
    {
        var items = new List<IListItem>
        {
            new ListItem(new NoOpCommand())
            {
                Title = _usageProvider.Descriptor.DisplayName,
                Subtitle = GetStatusSubtitle(state),
            },
        };
        items.AddRange(CreateActionItems(state));
        return items.ToArray();
    }

    private IListItem[] CreateItems(UsageSnapshot snapshot, UsageProviderState? state = null)
    {
        var now = DateTimeOffset.UtcNow;
        var estimatePrefix = snapshot.IsEstimate ? _localization.GetString("status.estimate", "Estimate: ") : string.Empty;
        var detailsTitle = _localization.GetString("details.limits", "Limits");
        var detailsMetadata = CreateDetailsMetadata(snapshot, state);
        var items = new List<IListItem>();
        if (snapshot.PrimaryWindow is not null)
        {
            var title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? _localization.Format("time.hours", 5)
                : UsageDisplayFormatter.GetWindowLabel(snapshot.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization);
            items.Add(CreateDataItem(title, $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now, _localization)}", detailsTitle, detailsMetadata));
        }

        if (snapshot.SecondaryWindow is not null)
        {
            var title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? _localization.GetString("window.weekly", "Weekly")
                : UsageDisplayFormatter.GetWindowLabel(snapshot.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization);
            items.Add(CreateDataItem(title, $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.SecondaryWindow, now, _localization)}", detailsTitle, detailsMetadata));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            items.Add(CreateDataItem(_localization.GetString("details.plan", "Plan"), snapshot.Plan, detailsTitle, detailsMetadata));
        }

        foreach (var additionalLimit in snapshot.AdditionalRateLimits)
        {
            items.Add(CreateDataItem(additionalLimit.Name, FormatAdditionalLimit(additionalLimit, now, estimatePrefix), detailsTitle, detailsMetadata));
        }

        foreach (var metric in snapshot.Metrics)
        {
            items.Add(CreateDataItem(UsageDisplayFormatter.GetMetricName(metric, _localization), UsageDisplayFormatter.FormatMetric(metric, _localization), detailsTitle, detailsMetadata));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Source))
        {
            items.Add(CreateDataItem(_localization.GetString("details.source", "Source"), FormatSafeSource(snapshot.Source), detailsTitle, detailsMetadata));
        }

        if (snapshot.FetchedAt is { } fetchedAt)
        {
            var subtitle = state?.IsRefreshing == true
                ? string.Concat(fetchedAt.ToLocalTime().ToString("g", _localization.Culture), " · ", _localization.GetString("status.refreshing", "Refreshing…"))
                : fetchedAt.ToLocalTime().ToString("g", _localization.Culture);
            items.Add(CreateDataItem(_localization.GetString("details.lastUpdated", "Last updated"), subtitle, detailsTitle, detailsMetadata));
        }

        if (state?.IsStale == true && (state.LastSuccessfulRefreshAt ?? snapshot.FetchedAt) is { } lastSuccessfulRefreshAt)
        {
            items.Add(CreateDataItem(_localization.GetString("details.lastSuccess", "Last successful refresh"), lastSuccessfulRefreshAt.ToLocalTime().ToString("g", _localization.Culture), detailsTitle, detailsMetadata));
        }

        items.AddRange(CreateActionItems(state));
        return items.ToArray();
    }

    private static ListItem CreateDataItem(string title, string subtitle, string detailsTitle, string detailsMetadata)
        => new(new NoOpCommand())
        {
            Title = title,
            Subtitle = subtitle,
            Details = new Details
            {
                Title = detailsTitle,
                Body = string.Concat("**", EscapeDetailsText(title), "**", Environment.NewLine, Environment.NewLine,
                    EscapeDetailsText(subtitle), Environment.NewLine, Environment.NewLine, detailsMetadata),
            },
        };

    private string CreateDetailsMetadata(UsageSnapshot snapshot, UsageProviderState? state)
    {
        var statusParts = new List<string>();
        if (state?.IsStale == true)
        {
            statusParts.Add(_localization.GetString("status.stale", "Stale"));
            statusParts.Add(GetStatusSubtitle(state));
        }
        if (state?.IsRefreshing == true) statusParts.Add(_localization.GetString("status.refreshing", "Refreshing…"));
        if (statusParts.Count == 0) statusParts.Add(_localization.GetString("status.ready", "Available"));
        var status = string.Join(" · ", statusParts);
        var lines = new List<string>
        {
            $"**{_localization.GetString("overview.status", "Status")}:** {EscapeDetailsText(status)}",
        };

        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            lines.Add($"**{_localization.GetString("details.plan", "Plan")}:** {EscapeDetailsText(snapshot.Plan)}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Source))
        {
            lines.Add($"**{_localization.GetString("details.source", "Source")}:** {EscapeDetailsText(FormatSafeSource(snapshot.Source))}");
        }

        if (snapshot.FetchedAt is { } fetchedAt)
        {
            lines.Add($"**{_localization.GetString("details.lastUpdated", "Last updated")}:** {fetchedAt.ToLocalTime().ToString("g", _localization.Culture)}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string EscapeDetailsText(string value)
    {
        var length = Math.Min(value.Length, 512);
        var escaped = new StringBuilder(length);
        foreach (var character in value.AsSpan(0, length))
        {
            switch (character)
            {
                case '\\': case '`': case '*': case '_': case '[': case ']': case '(': case ')': case '|':
                    escaped.Append('\\').Append(character);
                    break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '\r': case '\n': escaped.Append(' '); break;
                default:
                    if (!char.IsControl(character)) escaped.Append(character);
                    break;
            }
        }
        return escaped.ToString();
    }

    private IEnumerable<IListItem> CreateActionItems(UsageProviderState? state)
    {
        yield return new ListItem(new AnonymousCommand(() => _ = RefreshAsync()))
        {
            Title = _localization.GetString("action.refresh", "Refresh"),
            Subtitle = _localization.GetString("action.refreshSubtitle", "Fetch the latest provider snapshot."),
        };

        if (_stateSource is UsageSnapshotCache { SupportsConnectionValidation: true } cache)
        {
            yield return new ListItem(new AnonymousCommand(() => _ = ValidateConnectionAsync(cache)))
            {
                Title = _localization.GetString("action.validateConnection", "Validate connection"),
                Subtitle = _localization.GetString("action.validateConnectionSubtitle", "Send one deployment request to check connection; may consume quota."),
            };
        }

        if (Uri.TryCreate(_usageProvider.Descriptor.DashboardUrl, UriKind.Absolute, out var dashboardUrl)
            && dashboardUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ListItem(new OpenUrlCommand(dashboardUrl.AbsoluteUri))
            {
                Title = _localization.GetString("action.openDashboard", "Open provider dashboard"),
                Subtitle = _localization.GetString("action.openDashboardSubtitle", "Open the trusted provider website."),
            };
        }

        yield return new ListItem(new CopyTextCommand(BuildSafeDiagnostics(state)))
        {
            Title = _localization.GetString("action.copyDiagnostics", "Copy safe diagnostics"),
            Subtitle = _localization.GetString("action.copyDiagnosticsSubtitle", "Copy status without credentials."),
        };
    }

    private async Task ValidateConnectionAsync(UsageSnapshotCache cache)
    {
        if (IsDisposed || !IsActive) return;
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
            _logger($"[TokensLimits] Connection validation failed ({exception.GetType().Name}); provider state was updated.");
        }
    }

    private string BuildSafeDiagnostics(UsageProviderState? state)
    {
        var retryAfter = state?.RetryAfterUntil?.ToUniversalTime().ToString("O") ?? "none";
        return string.Join(Environment.NewLine,
            $"provider={_usageProvider.Descriptor.Id}",
            $"error={state?.ErrorKind.ToString() ?? "Unknown"}",
            $"stale={state?.IsStale ?? false}",
            $"retry_after={retryAfter}");
    }

    private string GetStatusSubtitle(UsageProviderState? state)
    {
        var unavailable = _localization.GetString("status.unavailable", "Limits unavailable");
        if (state is null || state.ErrorKind == UsageProviderErrorKind.None)
        {
            return unavailable;
        }

        return $"{unavailable} · {GetErrorLabel(state.ErrorKind)}. {GetNextStep(state)}";
    }

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

    private string GetNextStep(UsageProviderState state)
        => state.ErrorKind switch
        {
            UsageProviderErrorKind.MissingConfiguration
                => _localization.GetString("status.nextStep.configure", "Open extension settings and configure this provider."),
            UsageProviderErrorKind.Authentication
                => _localization.GetString("status.nextStep.authentication", "Check credentials or sign in again."),
            UsageProviderErrorKind.RateLimited when state.RetryAfterUntil is { } retryAfterUntil && retryAfterUntil > DateTimeOffset.UtcNow
                => _localization.Format("status.nextStep.rateLimitedUntil", retryAfterUntil.ToLocalTime().ToString("g", _localization.Culture)),
            UsageProviderErrorKind.RateLimited when state.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                => _localization.Format("status.nextStep.rateLimitedWithDelay", retryAfter.ToString("g", _localization.Culture)),
            UsageProviderErrorKind.RateLimited
                => _localization.GetString("status.nextStep.rateLimited", "Wait for the provider cooldown, then refresh."),
            UsageProviderErrorKind.Timeout or UsageProviderErrorKind.Network
                => _localization.GetString("status.nextStep.network", "Check the connection, then refresh."),
            UsageProviderErrorKind.UnsupportedResponse
                => _localization.GetString("status.nextStep.unsupported", "Copy safe diagnostics and check provider API compatibility."),
            _ => _localization.GetString("status.nextStep.retry", "Refresh or copy safe diagnostics."),
        };

    private static string GetCommandSignature(ICommand? command)
        => command is CopyTextCommand copy
            ? $"copy:{copy.Text}"
            : command?.GetType().FullName ?? string.Empty;

    private static string FormatSafeSource(string source)
    {
        var sources = source
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FormatSafeSourcePart)
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var formatted = string.Join(", ", sources);
        return formatted.Length <= 256 ? formatted : string.Concat(formatted.AsSpan(0, 253), "…");
    }

    private static string FormatSafeSourcePart(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return uri.IsFile
                ? Path.GetFileName(uri.LocalPath)
                : new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.GetLeftPart(UriPartial.Path);
        }

        if (Path.IsPathFullyQualified(source))
        {
            return Path.GetFileName(source);
        }

        var queryIndex = source.IndexOfAny(['?', '#']);
        return queryIndex >= 0 ? source[..queryIndex] : source;
    }

    private string FormatAdditionalLimit(AdditionalUsageLimit limit, DateTimeOffset now, string estimatePrefix)
    {
        var primaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization);
        var secondaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization);
        var primary = limit.PrimaryWindow is null ? $"{primaryLabel}: {_localization.GetString("overview.unavailable", "Data unavailable")}" : $"{primaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.PrimaryWindow, now, _localization)}";
        var secondary = limit.SecondaryWindow is null ? $"{secondaryLabel}: {_localization.GetString("overview.unavailable", "Data unavailable")}" : $"{secondaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.SecondaryWindow, now, _localization)}";
        return $"{estimatePrefix}{primary}; {secondary}";
    }
    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsActive) return;
        Title = $"{_usageProvider.Descriptor.DisplayName} {_localization.GetString("details.limits", "Limits")}";
        Name = _localization.Format("details.show", _usageProvider.Descriptor.DisplayName);
        PlaceholderText = Title;
        if (_stateSource is not null) ApplyState(_stateSource.State);
    }
    private static void LogMessage(string message) { Debug.WriteLine(message); ExtensionHost.LogMessage(message); }
}
