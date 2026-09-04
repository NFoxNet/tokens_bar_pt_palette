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
    private readonly ILocalizationService _localization;
    private readonly Timer _refreshTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IListItem[] _items;
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
        string? idSuffix = null,
        ILocalizationService? localization = null)
    {
        _usageProvider = usageProvider ?? throw new ArgumentNullException(nameof(usageProvider));
        _refreshSettings = refreshSettings;
        _logger = logger ?? LogMessage;
        _localization = localization ?? InvariantLocalizationService.Instance;
        Icon = ProviderIconCatalog.For(_usageProvider.Descriptor.Id);
        var baseId = _usageProvider.Descriptor.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "com.tokenslimits.codex.limits"
            : $"com.tokenslimits.provider.{_usageProvider.Descriptor.Id}.limits";
        Id = string.IsNullOrWhiteSpace(idSuffix) ? baseId : $"{baseId}.{idSuffix}";
        Title = $"{_usageProvider.Descriptor.DisplayName} {_localization.GetString("details.limits", "Limits")}";
        Name = _localization.Format("details.show", _usageProvider.Descriptor.DisplayName);
        PlaceholderText = Title;
        ShowDetails = true;
        _items = CreateLoadingItems();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;

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
                    Subtitle = _localization.Format("overview.unavailable", ex.Message),
                }], notify: true);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private IListItem[] CreateItems(UsageSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        var estimatePrefix = snapshot.IsEstimate ? _localization.GetString("status.estimate", "Estimate: ") : string.Empty;
        var items = new List<IListItem>();
        if (snapshot.PrimaryWindow is not null)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                    ? _localization.Format("time.hours", 5)
                    : UsageDisplayFormatter.GetWindowLabel(snapshot.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization),
                Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.PrimaryWindow, now, _localization)}",
            });
        }
        if (snapshot.SecondaryWindow is not null)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = snapshot.ProviderId.Equals("codex", StringComparison.OrdinalIgnoreCase)
                    ? _localization.GetString("window.weekly", "Weekly")
                    : UsageDisplayFormatter.GetWindowLabel(snapshot.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization),
                Subtitle = $"{estimatePrefix}{UsageDisplayFormatter.FormatRemainingWindow(snapshot.SecondaryWindow, now, _localization)}",
            });
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Plan))
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = _localization.GetString("details.plan", "Plan"),
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
                Title = GetMetricName(metric),
                Subtitle = UsageDisplayFormatter.FormatMetric(metric, _localization.Culture),
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
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
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

    private IListItem[] CreateLoadingItems()
        => [new ListItem(new NoOpCommand())
        {
            Title = _localization.GetString("details.limits", "Limits"),
            Subtitle = _localization.GetString("details.loading", "Loading…"),
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

    private string FormatAdditionalLimit(
        AdditionalUsageLimit limit,
        DateTimeOffset now,
        string estimatePrefix)
    {
        var primaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.PrimaryWindow, _localization.GetString("details.primary", "Primary"), _localization);
        var secondaryLabel = UsageDisplayFormatter.GetWindowLabel(limit.SecondaryWindow, _localization.GetString("details.secondary", "Additional"), _localization);
        var primary = limit.PrimaryWindow is null
            ? $"{primaryLabel}: {_localization.GetString("overview.unavailable", "Data unavailable")}"
            : $"{primaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.PrimaryWindow, now, _localization)}";
        var secondary = limit.SecondaryWindow is null
            ? $"{secondaryLabel}: {_localization.GetString("overview.unavailable", "Data unavailable")}"
            : $"{secondaryLabel}: {UsageDisplayFormatter.FormatRemainingWindow(limit.SecondaryWindow, now, _localization)}";
        return $"{estimatePrefix}{primary}; {secondary}";
    }

    private void LocalizationOnLanguageChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        Title = $"{_usageProvider.Descriptor.DisplayName} {_localization.GetString("details.limits", "Limits")}";
        Name = _localization.Format("details.show", _usageProvider.Descriptor.DisplayName);
        PlaceholderText = Title;
        if (_usageProvider.TryGetCachedSnapshot(out var snapshot))
        {
            SetItems(CreateItems(snapshot), notify: true);
        }
        else
        {
            SetItems(CreateLoadingItems(), notify: true);
        }
    }

    private string GetMetricName(UsageMetric metric)
        => string.Equals(metric.SemanticKey, "totalBalance", StringComparison.OrdinalIgnoreCase)
            ? _localization.GetString("metrics.totalBalance", "Total balance")
            : metric.Name;
}
