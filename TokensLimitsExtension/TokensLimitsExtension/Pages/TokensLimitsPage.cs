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

        _refreshTimer = new Timer(UsageRefreshHelpers.GetRefreshIntervalMilliseconds(_refreshSettings))
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
            if (_usageProvider.TryGetCachedSnapshot(out var cachedSnapshot))
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
            Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? "5ч"
                : UsageDisplayFormatter.GetWindowLabel(snapshot.PrimaryWindow, "Основное"),
            Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now)}",
        };
        var secondary = new ListItem(new NoOpCommand())
        {
            Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? "Еженедельно"
                : snapshot.SecondaryWindow is null
                    ? "Дополнительное"
                    : UsageDisplayFormatter.GetWindowLabel(snapshot.SecondaryWindow, "Дополнительное"),
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

        foreach (var metric in snapshot.Metrics)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = metric.Name,
                Subtitle = metric.Unit is null ? metric.Value : $"{metric.Value} {metric.Unit}",
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

        UsageRefreshHelpers.ApplySettingsChanged(
            _usageProvider,
            _refreshTimer,
            _refreshSettings,
            () => RefreshAsync());
    }

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
        var primaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.PrimaryWindow, "Основное");
        var secondaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.SecondaryWindow, "Дополнительное");
        var primary = limit.PrimaryWindow is null
            ? $"{primaryLabel}: данные недоступны"
            : $"{primaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.PrimaryWindow, now)}";
        var secondary = limit.SecondaryWindow is null
            ? $"{secondaryLabel}: данные недоступны"
            : $"{secondaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.SecondaryWindow, now)}";
        return $"{estimatePrefix}{primary}; {secondary}";
    }
}
