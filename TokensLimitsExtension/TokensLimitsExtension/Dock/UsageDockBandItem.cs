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
    private readonly Action<string> _logger;
    private readonly Timer _refreshTimer;
    private int _refreshInProgress;
    private int _disposed;

    public UsageDockBandItem(IUsageProvider provider, Action<string>? logger = null)
        : base(new NoOpCommand
        {
            Id = $"com.tokenslimits.provider.{provider?.Descriptor.Id}.dock",
            Name = provider?.Descriptor.DisplayName ?? "Usage limits",
        })
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? LogMessage;

        Title = _provider.Descriptor.DisplayName;
        Subtitle = "Загрузка лимитов...";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _refreshTimer = new Timer(TimeSpan.FromMinutes(1))
        {
            AutoReset = true,
        };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();

        _ = RefreshAsync();
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            var snapshot = await _provider.GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (IsDisposed)
            {
                return;
            }

            Title = snapshot.ProviderDisplayName;
            Subtitle = UsageDisplayFormatter.FormatCompactBandSubtitle(
                snapshot,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Subtitle = "Лимиты недоступны";
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
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        await RefreshAsync().ConfigureAwait(false);
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }
}
