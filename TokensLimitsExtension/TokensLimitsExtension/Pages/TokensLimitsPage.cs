using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Providers.Codex;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

public sealed partial class TokensLimitsPage : ListPage, IDisposable
{
    private readonly IUsageProvider _usageProvider;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly Action<string> _logger;
    private readonly Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IListItem[] _items = CreateLoadingItems();
    private int _refreshInProgress;
    private int _hasLoaded;
    private bool _disposed;

    public TokensLimitsPage(
        CodexUsageService usageService,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null)
        : this((ICodexUsageProvider)usageService, logger, refreshSettings)
    {
    }

    public TokensLimitsPage(
        ICodexUsageProvider usageService,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null)
        : this(new CodexUsageProviderAdapter(usageService), logger, refreshSettings)
    {
    }

    public TokensLimitsPage(
        IUsageProvider usageProvider,
        Action<string>? logger = null,
        IUsageRefreshSettings? refreshSettings = null)
    {
        _usageProvider = usageProvider ?? throw new ArgumentNullException(nameof(usageProvider));
        _refreshSettings = refreshSettings;
        _logger = logger ?? LogMessage;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Id = _usageProvider.Descriptor.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "com.tokenslimits.codex.limits"
            : $"com.tokenslimits.provider.{_usageProvider.Descriptor.Id}.limits";
        Title = $"{_usageProvider.Descriptor.DisplayName} limits";
        Name = $"Show {_usageProvider.Descriptor.DisplayName} usage limits";
        PlaceholderText = $"{_usageProvider.Descriptor.DisplayName} limits";
        ShowDetails = true;

        _refreshTimer = new Timer(GetRefreshIntervalMilliseconds())
        {
            AutoReset = true,
        };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
        _refreshTimer.Start();
        _refreshSettings?.Changed += RefreshSettingsOnChanged;
    }

    public override IListItem[] GetItems()
    {
        if (_disposed)
        {
            return [];
        }

        if (Volatile.Read(ref _hasLoaded) == 0)
        {
            if (_usageProvider is UsageSnapshotCache cache
                && cache.TryGetSnapshot(out var cachedSnapshot))
            {
                SetItems(CreateItems(cachedSnapshot), notify: false);
            }
            else
            {
                _ = RefreshAsync();
            }
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
            var snapshot = await _usageProvider.GetUsageSnapshotAsync(token).ConfigureAwait(false);
            if (!_disposed)
            {
                SetItems(CreateItems(snapshot), notify: true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _logger($"[TokensLimits] ERROR: {ex.Message}");
                SetItems([new ListItem(new NoOpCommand())
                {
                    Title = _usageProvider.Descriptor.DisplayName,
                    Subtitle = $"Не удалось получить лимиты: {ex.Message}",
                }], notify: true);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private static IListItem[] CreateItems(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var estimatePrefix = snapshot.IsEstimate ? "Оценка: " : string.Empty;
        var primary = new ListItem(new NoOpCommand())
        {
            Title = "5ч",
            Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now)}",
        };
        var secondary = new ListItem(new NoOpCommand())
        {
            Title = "Еженедельно",
            Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.SecondaryWindow, now)}",
        };

        var items = new List<IListItem> { primary, secondary };
        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = "План",
                Subtitle = snapshot.Plan,
            });
        }

        foreach (var additionalLimit in snapshot.AdditionalRateLimits)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = additionalLimit.Name,
                Subtitle = FormatAdditionalLimit(additionalLimit, now, estimatePrefix),
            });
        }

        return items.ToArray();
    }

    private void RefreshTimerOnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_disposed)
        {
            _ = RefreshAsync();
        }
    }

    private static void LogMessage(string message)
    {
        Debug.WriteLine(message);
        ExtensionHost.LogMessage(message);
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

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _refreshTimer.Interval = GetRefreshIntervalMilliseconds();
        if (_usageProvider is UsageSnapshotCache cache)
        {
            cache.Invalidate();
        }

        _ = RefreshAsync();
    }

    private double GetRefreshIntervalMilliseconds()
        => Math.Max(1000, (_refreshSettings?.RefreshInterval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds);

    private void SetItems(IListItem[] items, bool notify)
    {
        Volatile.Write(ref _items, items);
        Volatile.Write(ref _hasLoaded, 1);
        if (notify && !_disposed)
        {
            RaiseItemsChanged(items.Length);
        }
    }

    private static IListItem[] CreateLoadingItems()
        => [new ListItem(new NoOpCommand())
        {
            Title = "Лимиты",
            Subtitle = "Загрузка...",
        }];

    private static string FormatAdditionalLimit(
        AdditionalUsageLimit limit,
        DateTimeOffset now,
        string estimatePrefix)
    {
        var primary = limit.PrimaryWindow is null
            ? "5ч: данные недоступны"
            : $"5ч: {UsageDisplayFormatter.FormatRemainingWindow(limit.PrimaryWindow, now)}";
        var secondary = limit.SecondaryWindow is null
            ? "7д: данные недоступны"
            : $"7д: {UsageDisplayFormatter.FormatRemainingWindow(limit.SecondaryWindow, now)}";
        return $"{estimatePrefix}{primary}; {secondary}";
    }
}
