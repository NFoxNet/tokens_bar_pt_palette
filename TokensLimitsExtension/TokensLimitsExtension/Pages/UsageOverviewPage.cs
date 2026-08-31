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
    private int _refreshPending;
    private int _hasLoaded;
    private int _disposed;
    private long _providerVersion;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

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
        Id = "com.tokenslimits.overview";
        Title = "Tokens Limits";
        Name = "Show enabled provider limits";
        PlaceholderText = "Enabled providers";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _refreshTimer = new Timer(UsageRefreshHelpers.GetRefreshIntervalMilliseconds(refreshSettings)) { AutoReset = true };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();
        if (_refreshSettings is not null)
        {
            _refreshSettings.Changed += RefreshSettingsOnChanged;
        }
    }

    public override IListItem[] GetItems()
    {
        if (IsDisposed)
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
        if (IsDisposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            Volatile.Write(ref _refreshPending, 1);
            return;
        }

        using var linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token)
            : null;
        var token = linkedCts?.Token ?? _lifetimeCts.Token;
        try
        {
            do
            {
                Volatile.Write(ref _refreshPending, 0);
                IReadOnlyList<UsageSnapshotCache> caches;
                IReadOnlyList<TokensLimitsPage> pages;
                long providerVersion;
                lock (_providerGate)
                {
                    caches = _caches;
                    pages = _pages;
                    providerVersion = _providerVersion;
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

                lock (_providerGate)
                {
                    if (providerVersion == _providerVersion && !IsDisposed)
                    {
                        SetItems(items.ToArray());
                    }
                    else
                    {
                        Volatile.Write(ref _refreshPending, 1);
                    }
                }
            }
            while (!IsDisposed && Interlocked.Exchange(ref _refreshPending, 0) != 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
            if (!IsDisposed && Interlocked.Exchange(ref _refreshPending, 0) != 0)
            {
                _ = RefreshAsync(CancellationToken.None);
            }
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
            _providerVersion++;
        }

        Volatile.Write(ref _refreshPending, 1);
        Volatile.Write(ref _hasLoaded, 0);
        if (!IsDisposed)
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshSettings?.Changed -= RefreshSettingsOnChanged;
        _lifetimeCts.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Elapsed -= RefreshTimerOnElapsed;
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetItems(IListItem[] items)
    {
        if (IsDisposed)
        {
            return;
        }

        Volatile.Write(ref _items, items);
        Volatile.Write(ref _hasLoaded, 1);
        RaiseItemsChanged(items.Length);
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!IsDisposed)
        {
            _ = RefreshAsync();
        }
    }

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            UsageRefreshHelpers.ApplySettingsChanged(
                _refreshTimer,
                _refreshSettings,
                () => RefreshAsync());
        }
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
    }

    private sealed record ProviderResult(UsageSnapshot? Snapshot, string? Error);
}
