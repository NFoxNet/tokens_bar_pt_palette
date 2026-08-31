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
    private int _activated;
    private int _deactivated;
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool IsRefreshEnabled => !IsDisposed && Volatile.Read(ref _deactivated) == 0;

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
        IUsageRefreshSettings? refreshSettings = null,
        string? idSuffix = null)
    {
        _usageProvider = usageProvider ?? throw new ArgumentNullException(nameof(usageProvider));
        _refreshSettings = refreshSettings;
        _logger = logger ?? LogMessage;
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        var baseId = _usageProvider.Descriptor.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "com.tokenslimits.codex.limits"
            : $"com.tokenslimits.provider.{_usageProvider.Descriptor.Id}.limits";
        Id = string.IsNullOrWhiteSpace(idSuffix) ? baseId : $"{baseId}.{idSuffix}";
        Title = $"{_usageProvider.Descriptor.DisplayName} limits";
        Name = $"Show {_usageProvider.Descriptor.DisplayName} usage limits";
        PlaceholderText = $"{_usageProvider.Descriptor.DisplayName} limits";
        ShowDetails = true;

        _refreshTimer = new Timer(UsageRefreshHelpers.GetRefreshIntervalMilliseconds(_refreshSettings))
        {
            AutoReset = true,
        };
        _refreshTimer.Elapsed += RefreshTimerOnElapsed;
    }

    public override IListItem[] GetItems()
    {
        if (IsDisposed)
        {
            return [];
        }

        _ = EnsureActivated();

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
        if (!EnsureActivated() || Interlocked.Exchange(ref _refreshInProgress, 1) != 0)
        {
            return;
        }

        using var linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token)
            : null;
        var token = linkedCts?.Token ?? _lifetimeCts.Token;
        try
        {
            var snapshot = await _usageProvider.GetUsageSnapshotAsync(token).ConfigureAwait(false);
            if (IsRefreshEnabled)
            {
                SetItems(CreateItems(snapshot), notify: true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsRefreshEnabled)
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
        if (IsRefreshEnabled)
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Deactivate();
        _refreshTimer.Elapsed -= RefreshTimerOnElapsed;
        _refreshTimer.Dispose();
        GC.SuppressFinalize(this);
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

    private void SetItems(IListItem[] items, bool notify)
    {
        Volatile.Write(ref _items, items);
        Volatile.Write(ref _hasLoaded, 1);
        if (notify && IsRefreshEnabled)
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

    internal void Deactivate()
    {
        if (Interlocked.Exchange(ref _deactivated, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _activated, 0) != 0)
        {
            _refreshSettings?.Changed -= RefreshSettingsOnChanged;
            _refreshTimer.Stop();
        }

        _lifetimeCts.Cancel();
    }

    private bool EnsureActivated()
    {
        if (!IsRefreshEnabled)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _activated, 1) == 0)
        {
            _refreshTimer.Interval = UsageRefreshHelpers.GetRefreshIntervalMilliseconds(_refreshSettings);
            _refreshSettings?.Changed += RefreshSettingsOnChanged;
            _refreshTimer.Start();
        }

        return true;
    }

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
