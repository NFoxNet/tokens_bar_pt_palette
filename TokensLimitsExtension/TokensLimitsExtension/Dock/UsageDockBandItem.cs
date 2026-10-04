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
    private readonly CopyTextCommand _diagnosticsCommand;
    private readonly CommandContextItem _refreshMoreCommand;
    private readonly CommandContextItem? _dashboardMoreCommand;
    private readonly CommandContextItem _diagnosticsMoreCommand;
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
        _diagnosticsCommand = new CopyTextCommand(BuildSafeDiagnostics(_stateSource?.State));
        _refreshMoreCommand = new CommandContextItem(new AnonymousCommand(() => _ = ManualRefreshAsync()));
        _diagnosticsMoreCommand = new CommandContextItem(_diagnosticsCommand);
        _dashboardMoreCommand = Uri.TryCreate(_provider.Descriptor.DashboardUrl, UriKind.Absolute, out var dashboardUrl)
            && dashboardUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? new CommandContextItem(new OpenUrlCommand(dashboardUrl.AbsoluteUri))
                : null;
        UpdateMoreCommandLabels();
        var moreCommands = new System.Collections.Generic.List<IContextItem>
        {
            _refreshMoreCommand,
        };
        if (_dashboardMoreCommand is not null) moreCommands.Add(_dashboardMoreCommand);
        moreCommands.Add(_diagnosticsMoreCommand);
        MoreCommands = moreCommands.ToArray();
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

    private async Task ManualRefreshAsync()
    {
        if (IsDisposed || !IsActive) return;
        if (_stateSource is not null)
        {
            if (_coordinator is not null) await _coordinator.RefreshProviderAsync(_stateSource, force: true).ConfigureAwait(false);
            else await _stateSource.RefreshAsync(force: true).ConfigureAwait(false);
            ApplyState(_stateSource.State);
            return;
        }

        await RefreshAsync().ConfigureAwait(false);
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
        UpdateMoreCommandLabels();
        if (_stateSource is not null)
        {
            ApplyState(_stateSource.State);
        }
    }
    private void ApplyState(UsageProviderState state)
    {
        if (IsDisposed || !IsActive) return;
        UpdateDiagnostics();
        if (state.Snapshot is { } snapshot)
        {
            ApplySnapshot(snapshot);
            var statuses = new System.Collections.Generic.List<string>();
            if (state.IsRefreshing)
            {
                statuses.Add(_localization.GetString("status.refreshing", "Refreshing…"));
            }
            if (state.IsStale)
            {
                statuses.Add(GetStatusWarning(state));
            }

            if (statuses.Count > 0)
            {
                DockSubtitle = string.Concat(DockSubtitle, " · ", string.Join(" · ", statuses));
                Subtitle = DockSubtitle;
            }
        }
        else if (state.IsRefreshing)
        {
            Title = _provider.Descriptor.DisplayName;
            DockSubtitle = _localization.GetString("status.refreshingSubtitle", "Fetching the latest provider data.");
            Subtitle = DockSubtitle;
        }
        else ApplyUnavailable(state);
    }
    private void ApplySnapshot(UsageSnapshot snapshot) { if (IsDisposed || !IsActive) return; Title = snapshot.ProviderDisplayName; DockSubtitle = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot, _localization); Subtitle = DockSubtitle; }
    private void ApplyUnavailable(UsageProviderState? state = null)
    {
        if (IsDisposed || !IsActive) return;
        var unavailable = _localization.GetString("status.unavailable", "Limits unavailable");
        var suffix = state is { ErrorKind: not UsageProviderErrorKind.None } ? $" · {GetStatusWarning(state)}" : string.Empty;
        Subtitle = unavailable + suffix;
        DockSubtitle = Subtitle;
        UpdateDiagnostics();
    }

    private string GetStatusWarning(UsageProviderState state)
        => state.ErrorKind switch
        {
            UsageProviderErrorKind.MissingConfiguration => _localization.GetString("status.dock.configure", "Configure"),
            UsageProviderErrorKind.Authentication => _localization.GetString("status.dock.authentication", "Sign in"),
            UsageProviderErrorKind.RateLimited => _localization.GetString("status.dock.rateLimited", "Rate limited"),
            UsageProviderErrorKind.Timeout => _localization.GetString("status.dock.timeout", "Timed out"),
            UsageProviderErrorKind.Network => _localization.GetString("status.dock.network", "Offline"),
            UsageProviderErrorKind.UnsupportedResponse => _localization.GetString("status.dock.unsupported", "Unsupported response"),
            _ => _localization.GetString("status.stale", "Stale"),
        };

    private void UpdateDiagnostics()
    {
        if (!IsDisposed) _diagnosticsCommand.Text = BuildSafeDiagnostics(_stateSource?.State);
    }

    private void UpdateMoreCommandLabels()
    {
        _refreshMoreCommand.Title = _localization.GetString("action.refresh", "Refresh");
        _refreshMoreCommand.Subtitle = _localization.GetString("action.refreshSubtitle", "Fetch the latest provider snapshot.");
        if (_dashboardMoreCommand is not null)
        {
            _dashboardMoreCommand.Title = _localization.GetString("action.openDashboard", "Open provider dashboard");
            _dashboardMoreCommand.Subtitle = _localization.GetString("action.openDashboardSubtitle", "Open the trusted provider website.");
        }

        _diagnosticsMoreCommand.Title = _localization.GetString("action.copyDiagnostics", "Copy safe diagnostics");
        _diagnosticsMoreCommand.Subtitle = _localization.GetString("action.copyDiagnosticsSubtitle", "Copy status without credentials.");
    }

    private string BuildSafeDiagnostics(UsageProviderState? state)
        => string.Join(Environment.NewLine,
            $"provider={_provider.Descriptor.Id}",
            $"error={state?.ErrorKind.ToString() ?? "Unknown"}",
            $"stale={state?.IsStale ?? false}",
            $"retry_after_until={state?.RetryAfterUntil?.ToUniversalTime().ToString("O") ?? "none"}");
}
