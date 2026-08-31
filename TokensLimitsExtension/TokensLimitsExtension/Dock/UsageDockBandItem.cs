using System;
using System.Diagnostics;
using System.Timers;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

public sealed partial class UsageDockBandItem : ListItem, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly Action<string> _logger;
    private readonly Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _refreshInProgress;
    private int _deactivated;
    private int _disposed;

    public UsageDockBandItem(
        IUsageProvider provider,
        Action<string>? logger = null,
        ICommand? detailsCommand = null,
        IUsageRefreshSettings? refreshSettings = null)
        : base(detailsCommand ?? new NoOpCommand
        {
            Id = $"com.tokenslimits.provider.{provider?.Descriptor.Id}.dock",
            Name = provider?.Descriptor.DisplayName ?? "Usage limits",
        })
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _refreshSettings = refreshSettings;
        _logger = logger ?? LogMessage;

        Title = _provider.Descriptor.DisplayName;
        Subtitle = "Загрузка лимитов...";
        DockSubtitle = "Загрузка лимитов...";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _refreshTimer = new Timer(UsageRefreshHelpers.GetRefreshIntervalMilliseconds(_refreshSettings))
        {
            AutoReset = true,
        };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();
        _refreshSettings?.Changed += RefreshSettingsOnChanged;

        _ = RefreshAsync();
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool IsRefreshEnabled => !IsDisposed && Volatile.Read(ref _deactivated) == 0;

    public string DockSubtitle { get; private set => SetProperty(ref field, value); } = string.Empty;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRefreshEnabled || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            using var linkedCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token)
                : null;
            var token = linkedCts?.Token ?? _lifetimeCts.Token;
            var snapshot = await _provider.GetUsageSnapshotAsync(token).ConfigureAwait(false);
            if (!IsRefreshEnabled)
            {
                return;
            }

            Title = snapshot.ProviderDisplayName;
            DockSubtitle = UsageDisplayFormatter.FormatDockBandSubtitle(snapshot);
            Subtitle = DockSubtitle;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRefreshEnabled)
            {
                Subtitle = "Лимиты недоступны";
                DockSubtitle = "Лимиты недоступны";
                _logger($"[TokensLimits] ERROR: {ex.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Deactivate();
        _refreshTimer.Elapsed -= RefreshTimerOnElapsed;
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (IsRefreshEnabled)
        {
            _ = RefreshAsync();
        }
    }

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        if (!IsRefreshEnabled)
        {
            return;
        }

        UsageRefreshHelpers.ApplySettingsChanged(
            _refreshTimer,
            _refreshSettings,
            () => RefreshAsync());
    }

    internal void Deactivate()
    {
        if (Interlocked.Exchange(ref _deactivated, 1) != 0)
        {
            return;
        }

        _refreshTimer.Stop();
        _refreshSettings?.Changed -= RefreshSettingsOnChanged;
        _lifetimeCts.Cancel();
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }
}
