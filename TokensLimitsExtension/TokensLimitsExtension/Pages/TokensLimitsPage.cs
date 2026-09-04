using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        if (IsDisposed) return [];
        if (_stateSource?.State.Snapshot is { } snapshot) SetItems(CreateItems(snapshot), false);
        else _ = RefreshAsync();
        return Volatile.Read(ref _items);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return;
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
        if (_stateSource is not null) _stateSource.StateChanged -= StateSourceOnStateChanged;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        GC.SuppressFinalize(this);
    }
    internal void Deactivate() => Dispose();
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private void StateSourceOnStateChanged(object? sender, EventArgs e) => ApplyState(_stateSource!.State);
    private void ApplyState(UsageProviderState state)
    {
        if (IsDisposed) return;
        if (state.Snapshot is { } snapshot) SetItems(CreateItems(snapshot), true);
        else if (!state.IsRefreshing) SetItems(CreateUnavailableItems(), true);
    }
    private void SetItems(IListItem[] items, bool notify)
    {
        Volatile.Write(ref _items, items);
        if (notify && !IsDisposed) RaiseItemsChanged(items.Length);
    }
    private IListItem[] CreateLoadingItems() => [new ListItem(new NoOpCommand()) { Title = _localization.GetString("details.limits", "Limits"), Subtitle = _localization.GetString("details.loading", "Loading…") }];
    private IListItem[] CreateUnavailableItems() => [new ListItem(new NoOpCommand()) { Title = _usageProvider.Descriptor.DisplayName, Subtitle = _localization.GetString("status.unavailable", "Limits unavailable") }];
    private IListItem[] CreateItems(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var estimatePrefix = snapshot.IsEstimate ? _localization.GetString("status.estimate", "Estimate: ") : string.Empty;
        var items = new List<IListItem>();
        if (snapshot.PrimaryWindow is not null) items.Add(new ListItem(new NoOpCommand()) { Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? _localization.Format("time.hours", 5) : UsageDisplayFormatter.GetWindowLabel(snapshot.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization), Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now, _localization)}" });
        if (snapshot.SecondaryWindow is not null) items.Add(new ListItem(new NoOpCommand()) { Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase) ? _localization.GetString("window.weekly", "Weekly") : UsageDisplayFormatter.GetWindowLabel(snapshot.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization), Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.SecondaryWindow, now, _localization)}" });
        if (!string.IsNullOrWhiteSpace(snapshot.Plan)) items.Add(new ListItem(new NoOpCommand()) { Title = _localization.GetString("details.plan", "Plan"), Subtitle = snapshot.Plan });
        foreach (var additionalLimit in snapshot.AdditionalRateLimits) items.Add(new ListItem(new NoOpCommand()) { Title = additionalLimit.Name, Subtitle = FormatAdditionalLimit(additionalLimit, now, estimatePrefix) });
        foreach (var metric in snapshot.Metrics) items.Add(new ListItem(new NoOpCommand()) { Title = string.Equals(metric.SemanticKey, "totalBalance", StringComparison.OrdinalIgnoreCase) ? _localization.GetString("metrics.totalBalance", "Total balance") : metric.Name, Subtitle = UsageDisplayFormatter.FormatMetric(metric, _localization.Culture) });
        return items.ToArray();
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
        if (IsDisposed) return;
        Title = $"{_usageProvider.Descriptor.DisplayName} {_localization.GetString("details.limits", "Limits")}";
        Name = _localization.Format("details.show", _usageProvider.Descriptor.DisplayName);
        PlaceholderText = Title;
        if (_stateSource?.State.Snapshot is { } snapshot) SetItems(CreateItems(snapshot), true);
    }
    private static void LogMessage(string message) { Debug.WriteLine(message); ExtensionHost.LogMessage(message); }
}
