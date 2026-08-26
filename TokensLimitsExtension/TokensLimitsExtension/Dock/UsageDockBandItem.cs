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

        _refreshTimer = new Timer(GetRefreshIntervalMilliseconds())
        {
            AutoReset = true,
        };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();
        _refreshSettings?.Changed += RefreshSettingsOnChanged;

        _ = RefreshAsync();
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public string DockSubtitle { get; private set => SetProperty(ref field, value); } = string.Empty;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
            var snapshot = await _provider.GetUsageSnapshotAsync(token).ConfigureAwait(false);
            if (IsDisposed)
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
            if (!IsDisposed)
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

        _refreshTimer.Stop();
        _refreshTimer.Elapsed -= RefreshTimerOnElapsed;
        _refreshSettings?.Changed -= RefreshSettingsOnChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        _ = RefreshAsync();
    }

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        _refreshTimer.Interval = GetRefreshIntervalMilliseconds();
        if (_provider is UsageSnapshotCache cache)
        {
            cache.Invalidate();
        }

        _ = RefreshAsync();
    }

    private double GetRefreshIntervalMilliseconds()
        => Math.Max(1000, (_refreshSettings?.RefreshInterval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds);

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }
}
