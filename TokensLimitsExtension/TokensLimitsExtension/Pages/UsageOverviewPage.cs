using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;
using Timer = System.Timers.Timer;

namespace TokensLimitsExtension;

/// <summary>
/// The single top-level command. It lists all enabled providers and navigates
/// to the provider-specific detail page when an item is selected.
/// </summary>
public sealed partial class UsageOverviewPage : ListPage, IDisposable
{
    private IReadOnlyList<UsageSnapshotCache> _caches;
    private IReadOnlyList<TokensLimitsPage> _pages;
    private readonly object _providerGate = new();
    private readonly Action<string> _logger;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IListItem[] _items = [new ListItem(new NoOpCommand()) { Title = "Провайдеры", Subtitle = "Загрузка..." }];
    private int _refreshInProgress;
    private int _hasLoaded;
    private bool _disposed;

    public UsageOverviewPage(
        IReadOnlyList<UsageSnapshotCache> caches,
        IReadOnlyList<TokensLimitsPage> pages,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null)
    {
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        if (_caches.Count != _pages.Count)
        {
            throw new ArgumentException("Provider caches and pages must have the same length.");
        }

        _logger = logger ?? LogMessage;
        _refreshSettings = refreshSettings;
        Title = "Tokens Limits";
        Name = "Show enabled provider limits";
        PlaceholderText = "Enabled providers";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _refreshTimer = new Timer(GetRefreshIntervalMilliseconds(refreshSettings)) { AutoReset = true };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();
        if (_refreshSettings is not null)
        {
            _refreshSettings.Changed += RefreshSettingsOnChanged;
        }
    }

    public override IListItem[] GetItems()
    {
        if (_disposed)
        {
            return [];
        }

        if (Volatile.Read(ref _hasLoaded) == 0)
        {
            _ = RefreshAsync();
        }

        return Volatile.Read(ref _items);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        var token = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        try
        {
            IReadOnlyList<UsageSnapshotCache> caches;
            IReadOnlyList<TokensLimitsPage> pages;
            lock (_providerGate)
            {
                caches = _caches;
                pages = _pages;
            }

            var results = await Task.WhenAll(caches.Select(cache => ReadProviderAsync(cache, token))).ConfigureAwait(false);
            var items = new List<IListItem>();
            for (var index = 0; index < results.Length; index++)
            {
                var result = results[index];
                items.Add(new ListItem(pages[index])
                {
                    Title = caches[index].Descriptor.DisplayName,
                    Subtitle = result.Error is null
                        ? UsageDisplayFormatter.FormatDockBandSubtitle(result.Snapshot!)
                        : result.Error,
                });
            }

            if (items.Count == 0)
            {
                items.Add(new ListItem(new NoOpCommand())
                {
                    Title = "Нет включённых провайдеров",
                    Subtitle = "Включите провайдеров в настройках расширения.",
                });
            }

            SetItems(items.ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    public void UpdateProviders(
        IReadOnlyList<UsageSnapshotCache> caches,
        IReadOnlyList<TokensLimitsPage> pages)
    {
        ArgumentNullException.ThrowIfNull(caches);
        ArgumentNullException.ThrowIfNull(pages);
        if (caches.Count != pages.Count)
        {
            throw new ArgumentException("Provider caches and pages must have the same length.");
        }

        lock (_providerGate)
        {
            _caches = caches;
            _pages = pages;
        }

        Volatile.Write(ref _hasLoaded, 0);
        if (!_disposed)
        {
            _ = RefreshAsync();
        }
    }

    private async Task<ProviderResult> ReadProviderAsync(UsageSnapshotCache cache, CancellationToken token)
    {
        try
        {
            return new ProviderResult(await cache.GetUsageSnapshotAsync(token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger($"[TokensLimits] ERROR: {cache.Descriptor.Id}: {ex.Message}");
            return new ProviderResult(null, $"Данные недоступны: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshSettings?.Changed -= RefreshSettingsOnChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _refreshTimer.Stop();
        _refreshTimer.Elapsed -= RefreshTimerOnElapsed;
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetItems(IListItem[] items)
    {
        if (_disposed)
        {
            return;
        }

        Volatile.Write(ref _items, items);
        Volatile.Write(ref _hasLoaded, 1);
        RaiseItemsChanged(items.Length);
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_disposed)
        {
            _ = RefreshAsync();
        }
    }

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            _refreshTimer.Interval = GetRefreshIntervalMilliseconds(_refreshSettings);
            _ = RefreshAsync();
        }
    }

    private static double GetRefreshIntervalMilliseconds(IUsageRefreshSettings? settings)
        => Math.Max(1000, (settings?.RefreshInterval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds);

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

    private sealed record ProviderResult(UsageSnapshot? Snapshot, string? Error);
}
