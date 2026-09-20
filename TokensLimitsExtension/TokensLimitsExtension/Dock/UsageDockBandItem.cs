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
    private readonly object _lifecycleGate = new();
    private int _isActive = 1;
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
    internal bool IsActive => Volatile.Read(ref _isActive) != 0;
    public string DockSubtitle { get; private set => SetProperty(ref field, value); } = string.Empty;
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
        try { ApplySnapshot(await _provider.GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false)); }
        catch { ApplyUnavailable(); }
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
                if (_stateSource is not null)
                {
                    if (active) _stateSource.StateChanged += StateSourceOnStateChanged;
                    else _stateSource.StateChanged -= StateSourceOnStateChanged;
                }

                if (active) _localization.LanguageChanged += LocalizationOnLanguageChanged;
                else _localization.LanguageChanged -= LocalizationOnLanguageChanged;
            }
        }

        if (active)
        {
            SynchronizeState();
        }
        else if (changed && !IsDisposed)
        {
            var unavailable = _localization.GetString("status.unavailable", "Limits unavailable");
            Subtitle = unavailable;
            DockSubtitle = unavailable;
        }
    }

    internal void SynchronizeState()
    {
        if (!IsDisposed && IsActive && _stateSource is not null)
        {
            ApplyState(_stateSource.State);
        }
    }

    private void StateSourceOnStateChanged(object? sender, EventArgs e) => ApplyState(_stateSource!.State);
    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsActive) return;
        if (_stateSource is not null)
        {
            ApplyState(_stateSource.State);
        }
    }
    private void ApplyState(UsageProviderState state)
    {
        if (IsDisposed || !IsActive) return;
        if (state.Snapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
            if (state.IsStale)
            {
                DockSubtitle = $"{DockSubtitle} · {GetStatusWarning(state)}";
                Subtitle = DockSubtitle;
            }
        }
        else if (!state.IsRefreshing) ApplyUnavailable(state);
    }
    private void ApplySnapshot(UsageSnapshot snapshot) { if (IsDisposed || !IsActive) return; Title = snapshot.ProviderDisplayName; DockSubtitle = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, _localization); Subtitle = DockSubtitle; }
    private void ApplyUnavailable(UsageProviderState? state = null)
    {
        if (IsDisposed || !IsActive) return;
        var unavailable = _localization.GetString("status.unavailable", "Limits unavailable");
        var suffix = state is { ErrorKind: not UsageProviderErrorKind.None } ? $" · {GetStatusWarning(state)}" : string.Empty;
        Subtitle = unavailable + suffix;
        DockSubtitle = Subtitle;
    }

    private string GetStatusWarning(UsageProviderState state)
        => state.ErrorKind switch
        {
            UsageProviderErrorKind.MissingConfiguration => _localization.GetString("status.dock.configure", "Configure"),
            UsageProviderErrorKind.Authentication => _localization.GetString("status.dock.authentication", "Sign in"),
            UsageProviderErrorKind.RateLimited => _localization.GetString("status.dock.rateLimited", "Rate limited"),
            UsageProviderErrorKind.Timeout or UsageProviderErrorKind.Network => _localization.GetString("status.dock.network", "Offline"),
            UsageProviderErrorKind.UnsupportedResponse => _localization.GetString("status.dock.unsupported", "Unsupported response"),
            _ => _localization.GetString("status.stale", "Stale"),
        };
}
