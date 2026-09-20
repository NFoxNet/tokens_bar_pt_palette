using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        _items = CreateLoadingItems();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (_stateSource is not null) _stateSource.StateChanged += StateSourceOnStateChanged;
    }

    public override IListItem[] GetItems()
    {
        if (IsDisposed || !IsActive) return [];
        return Volatile.Read(ref _items);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || !IsActive) return;
        if (_stateSource is not null)
        {
            if (_coordinator is not null) await _coordinator.RefreshProviderAsync(_stateSource).ConfigureAwait(false);
            else await _stateSource.RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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
                    Volatile.Write(ref _items, []);
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
        if (IsDisposed || !IsActive) return;
        if (state.Snapshot is { } snapshot)
        {
            var items = new List<IListItem>(CreateItems(snapshot, state));
            if (state.IsStale)
            {
                items.Insert(0, new ListItem(new NoOpCommand())
                {
                    Title = _localization.GetString("status.stale", "Stale"),
                    Subtitle = GetStatusSubtitle(state),
                });
            }
            SetItems([.. items], true);
        }
        else if (!state.IsRefreshing) SetItems(CreateUnavailableItems(state), true);
    }
    private void SetItems(IListItem[] items, bool notify)
    {
        if (IsDisposed || !IsActive) return;
        var signature = string.Join('\u001f', items.Select(item => item is ListItem listItem
            ? $"{listItem.Title}\u001e{listItem.Subtitle}\u001e{GetCommandSignature(listItem.Command)}"
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
        var items = new List<IListItem>();
        if (snapshot.PrimaryWindow is not null) items.Add(new ListItem(new NoOpCommand()) { Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? _localization.Format("time.hours", 5) : UsageDisplayFormatter.GetWindowLabel(snapshot.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization), Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now, _localization)}" });
        if (snapshot.SecondaryWindow is not null) items.Add(new ListItem(new NoOpCommand()) { Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? _localization.GetString("window.weekly", "Weekly") : UsageDisplayFormatter.GetWindowLabel(snapshot.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization), Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.SecondaryWindow, now, _localization)}" });
        if (!string.IsNullOrWhiteSpace(snapshot.Plan)) items.Add(new ListItem(new NoOpCommand()) { Title = _localization.GetString("details.plan", "Plan"), Subtitle = snapshot.Plan });
        foreach (var additionalLimit in snapshot.AdditionalRateLimits) items.Add(new ListItem(new NoOpCommand()) { Title = additionalLimit.Name, Subtitle = FormatAdditionalLimit(additionalLimit, now, estimatePrefix) });
        foreach (var metric in snapshot.Metrics) items.Add(new ListItem(new NoOpCommand()) { Title = UsageDisplayFormatter.GetMetricName(metric, _localization), Subtitle = UsageDisplayFormatter.FormatMetric(metric, _localization.Culture) });
        if (!string.IsNullOrWhiteSpace(snapshot.Source))
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = _localization.GetString("details.source", "Source"),
                Subtitle = FormatSafeSource(snapshot.Source),
            });
        }

        if (state?.IsStale == true && (state.LastSuccessfulRefreshAt ?? snapshot.FetchedAt) is { } lastSuccessfulRefreshAt)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = _localization.GetString("details.lastSuccess", "Last successful refresh"),
                Subtitle = lastSuccessfulRefreshAt.ToLocalTime().ToString("g", _localization.Culture),
            });
        }

        items.AddRange(CreateActionItems(state));
        return items.ToArray();
    }

    private IEnumerable<IListItem> CreateActionItems(UsageProviderState? state)
    {
        yield return new ListItem(new AnonymousCommand(() => _ = RefreshAsync()))
        {
            Title = _localization.GetString("action.refresh", "Refresh"),
            Subtitle = _localization.GetString("action.refreshSubtitle", "Fetch the latest provider snapshot."),
        };

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

    private string BuildSafeDiagnostics(UsageProviderState? state)
    {
        var retryAfter = state?.RetryAfter?.ToString() ?? "none";
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

        return $"{unavailable}. {GetNextStep(state)}";
    }

    private string GetNextStep(UsageProviderState state)
        => state.ErrorKind switch
        {
            UsageProviderErrorKind.MissingConfiguration
                => _localization.GetString("status.nextStep.configure", "Open extension settings and configure this provider."),
            UsageProviderErrorKind.Authentication
                => _localization.GetString("status.nextStep.authentication", "Check credentials or sign in again."),
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
                : uri.GetLeftPart(UriPartial.Path);
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
