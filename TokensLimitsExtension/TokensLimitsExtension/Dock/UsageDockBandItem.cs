using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>Dock projection of a shared provider cache; it never owns a timer.</summary>
public sealed partial class UsageDockBandItem : ListItem, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly IUsageProviderStateSource? _stateSource;
    private readonly UsageRefreshCoordinator? _coordinator;
    private readonly ILocalizationService _localization;
    private int _disposed;

    public UsageDockBandItem(IUsageProvider provider, Action<string>? logger = null, ICommand? detailsCommand = null, IUsageRefreshSettings? refreshSettings = null, ILocalizationService? localization = null, UsageRefreshCoordinator? coordinator = null)
        : base(detailsCommand ?? new NoOpCommand { Id = $"com.tokenslimits.provider.{provider?.Descriptor.Id}.dock", Name = provider?.Descriptor.DisplayName ?? "Usage limits" })
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _stateSource = provider as IUsageProviderStateSource;
        _coordinator = coordinator;
        _localization = localization ?? InvariantLocalizationService.Instance;
        Title = _provider.Descriptor.DisplayName;
        Subtitle = _localization.GetString("details.loading", "Loading…");
        DockSubtitle = Subtitle;
        Icon = ProviderIconCatalog.For(_provider.Descriptor.Id);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (_stateSource is not null) _stateSource.StateChanged += StateSourceOnStateChanged;
        _ = RefreshAsync();
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public string DockSubtitle { get; private set => SetProperty(ref field, value); } = string.Empty;
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
        try { ApplySnapshot(await _provider.GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false)); }
        catch { ApplyUnavailable(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_stateSource is not null) _stateSource.StateChanged -= StateSourceOnStateChanged;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        GC.SuppressFinalize(this);
    }
    internal void Deactivate() => Dispose();
    private void StateSourceOnStateChanged(object? sender, EventArgs e) => ApplyState(_stateSource!.State);
    private void LocalizationOnLanguageChanged(object? sender, EventArgs e) { if (_stateSource?.State.Snapshot is { } snapshot) ApplySnapshot(snapshot); }
    private void ApplyState(UsageProviderState state)
    {
        if (IsDisposed) return;
        if (state.Snapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
            if (state.IsStale)
            {
                DockSubtitle = $"{DockSubtitle} · {_localization.GetString("status.stale", "Stale")}";
                Subtitle = DockSubtitle;
            }
        }
        else if (!state.IsRefreshing) ApplyUnavailable();
    }
    private void ApplySnapshot(UsageSnapshot snapshot) { if (IsDisposed) return; Title = snapshot.ProviderDisplayName; DockSubtitle = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, _localization); Subtitle = DockSubtitle; }
    private void ApplyUnavailable() { Subtitle = _localization.GetString("status.unavailable", "Limits unavailable"); DockSubtitle = Subtitle; }
}
