using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension;

/// <summary>Displays a bounded, local-only view of saved usage measurements.</summary>
public sealed partial class UsageHistoryPage : ListPage, IDisposable
{
    private readonly UsageHistoryController _controller;
    private readonly ILocalizationService _localization;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private IReadOnlyList<UsageSnapshotCache> _caches;
    private IListItem[] _items = [];
    private (int Index, string ProviderId, string ProviderName, IReadOnlyList<UsageHistoryEntry> Entries)[] _paceRows = [];
    private int _disposed;

    public UsageHistoryPage(
        UsageHistoryController controller,
        IReadOnlyList<UsageSnapshotCache> caches,
        ILocalizationService? localization = null,
        TimeProvider? timeProvider = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _localization = localization ?? InvariantLocalizationService.Instance;
        Id = "com.tokenslimits.history";
        Title = _localization.GetString("app.title", "Tokens Limits");
        Name = _localization.GetString("history.title", "Usage history");
        PlaceholderText = _localization.GetString("history.empty", "No saved measurements yet.");
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        ShowDetails = true;
        _controller.Changed += ControllerOnChanged;
        _controller.PaceChanged += ControllerOnPaceChanged;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        _controller.UpdateProviders(caches);
        RebuildItems();
    }

    public override IListItem[] GetItems()
    {
        IListItem[] items;
        (int Index, string ProviderId, string ProviderName, IReadOnlyList<UsageHistoryEntry> Entries)[] paceRows;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return [];
            items = (IListItem[])_items.Clone();
            paceRows = _paceRows;
        }
        foreach (var row in paceRows)
            items[row.Index] = CreatePaceItem(row.ProviderId, row.ProviderName, row.Entries);
        return items;
    }

    public void UpdateProviders(IReadOnlyList<UsageSnapshotCache> caches)
    {
        ArgumentNullException.ThrowIfNull(caches);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _caches = caches;
        }
        _controller.UpdateProviders(caches);
        RebuildItems();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _controller.Changed -= ControllerOnChanged;
        _controller.PaceChanged -= ControllerOnPaceChanged;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        lock (_gate)
        {
            _items = [];
            _paceRows = [];
        }
    }

    private void ControllerOnChanged(object? sender, EventArgs args) => RebuildItems();

    private void ControllerOnPaceChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0) RaiseItemsChanged();
    }

    private void LocalizationOnLanguageChanged(object? sender, EventArgs args)
    {
        Name = _localization.GetString("history.title", "Usage history");
        PlaceholderText = _localization.GetString("history.empty", "No saved measurements yet.");
        RebuildItems();
    }

    private void RebuildItems()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        UsageSnapshotCache[] caches;
        lock (_gate) caches = _caches.ToArray();

        var items = new List<IListItem>
        {
            new ListItem(new AnonymousCommand(ClearHistory))
            {
                Title = _localization.GetString("history.clear", "Clear history"),
                Subtitle = _localization.GetString("history.clear.description", "Delete saved usage measurements"),
            },
        };

        var paceRows = new List<(int Index, string ProviderId, string ProviderName, IReadOnlyList<UsageHistoryEntry> Entries)>();
        foreach (var cache in caches)
        {
            var entries = _controller.ReadRecent(cache.Descriptor.Id, limit: 20);
            if (entries.Count > 0)
            {
                var chronologicalEntries = entries.Reverse().ToArray();
                paceRows.Add((items.Count, cache.Descriptor.Id, cache.Descriptor.DisplayName, chronologicalEntries));
                items.Add(CreatePaceItem(cache.Descriptor.Id, cache.Descriptor.DisplayName, chronologicalEntries));
            }
            foreach (var entry in entries)
            {
                items.Add(CreateEntryItem(cache.Descriptor.DisplayName, entry));
            }
        }

        if (_controller.LastErrorKind is { } error)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = _localization.GetString("history.error.title", "History storage error"),
                Subtitle = ErrorText(error),
            });
        }
        else if (items.Count == 1)
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = _localization.GetString("history.empty", "No saved measurements yet."),
            });
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _items = items.ToArray();
            _paceRows = paceRows.ToArray();
        }
        RaiseItemsChanged();
    }

    private ListItem CreatePaceItem(string providerId, string providerName, IReadOnlyList<UsageHistoryEntry> chronologicalEntries)
    {
        var now = _timeProvider.GetUtcNow();
        var estimate = _controller.EstimatePace(providerId, chronologicalEntries, now);
        var latest = chronologicalEntries[^1];
        var latestWindow = estimate?.IsPrimaryWindow == false ? latest.SecondaryWindow : latest.PrimaryWindow;
        var subtitle = estimate is null
            ? _localization.GetString("history.pace.insufficient", "Not enough recent fixed-window history")
            : BuildPaceText(estimate, latestWindow, now);
        var comparison = estimate is null
            ? null
            : string.Format(_localization.Culture,
                _localization.GetString("history.pace.comparison", "Usage now: {0}% · even pace by now: {1}%"),
                latestWindow?.UsedPercent.ToString("0.#", _localization.Culture) ?? "?",
                estimate.ExpectedUsedPercent.ToString("0.#", _localization.Culture));

        return new ListItem(new NoOpCommand())
        {
            Title = string.Format(_localization.Culture,
                _localization.GetString("history.pace.title", "Usage pace · {0}"), providerName),
            Subtitle = subtitle,
            Details = new Details
            {
                Title = providerName,
                Body = string.Join(Environment.NewLine, new[] { subtitle, comparison }.Where(line => line is not null)),
            },
        };
    }

    private string BuildPaceText(UsagePaceEstimate estimate, UsageHistoryWindow? latestWindow, DateTimeOffset now)
    {
        var estimateLabel = _localization.GetString("history.estimate", "Estimate");
        var outcome = latestWindow?.ResetAt is { } resetAt
            && estimate.TimeUntilExhaustion > resetAt - now
            ? _localization.GetString("history.pace.noExhaustionBeforeReset", "At current pace, no exhaustion is expected before reset")
            : string.Format(_localization.Culture,
                _localization.GetString("history.pace.exhaustion", "Estimated exhaustion in {0} at {1}% per hour"),
                estimate.TimeUntilExhaustion.ToString("g", _localization.Culture),
                estimate.PercentPerHour.ToString("0.#", _localization.Culture));
        return estimateLabel + " · " + outcome;
    }
    private ListItem CreateEntryItem(string providerName, UsageHistoryEntry entry)
    {
        var window = entry.PrimaryWindow ?? entry.SecondaryWindow;
        var value = window is null
            ? string.Empty
            : string.Format(_localization.Culture, "{0:0.#}%", window.UsedPercent);
        var timestamp = entry.Timestamp.ToLocalTime().ToString("g", _localization.Culture);
        var sourceKey = entry.SourceKind switch
        {
            UsageHistorySourceKind.Api => "history.source.api",
            UsageHistorySourceKind.Local => "history.source.local",
            _ => "history.source.unknown",
        };
        var sourceFallback = entry.SourceKind switch
        {
            UsageHistorySourceKind.Api => "API",
            UsageHistorySourceKind.Local => "Local",
            _ => "Unknown",
        };
        var source = _localization.GetString(sourceKey, sourceFallback);
        var estimate = entry.IsEstimate ? " · " + _localization.GetString("history.estimate", "Estimate") : string.Empty;
        var segment = string.Format(_localization.Culture, _localization.GetString("history.segment", "Series {0}"), entry.SegmentId.ToString("N")[..8]);
        var metrics = entry.Metrics.Select(FormatMetric);
        var detailLines = new List<string?> { timestamp, source + estimate, segment };
        if (window is not null)
        {
            detailLines.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.###}%", window.UsedPercent));
            if (window.IsForecastEligible)
            {
                detailLines.Add(_localization.GetString("history.forecastEligible", "Fixed window with explicit start and reset"));
            }
        }
        detailLines.AddRange(metrics);
        return new ListItem(new NoOpCommand())
        {
            Title = string.Join(" · ", new[] { providerName, value }.Where(part => part.Length > 0)),
            Subtitle = string.Join(" · ", new[] { timestamp, source + estimate }),
            Details = new Details
            {
                Title = providerName,
                Body = string.Join(Environment.NewLine, detailLines.Where(line => line is not null)),
            },
        };
    }

    private string? FormatMetric(UsageHistoryMetric metric)
    {
        var key = metric.SemanticKey switch
        {
            "tokens5h" => "history.metric.tokens5h",
            "tokens7d" => "history.metric.tokens7d",
            "balance" => "history.metric.balance",
            "totalBalance" => "history.metric.totalBalance",
            "quotaUnavailable" => "history.metric.quotaUnavailable",
            "connectionStatus" => "history.metric.connectionStatus",
            _ => null,
        };
        if (key is null) return null;

        var name = _localization.GetString(key, metric.SemanticKey);
        var number = metric.NumericValue?.ToString("0.###", _localization.Culture)
            ?? metric.Used?.ToString("0.###", _localization.Culture)
            ?? metric.Limit?.ToString("0.###", _localization.Culture)
            ?? metric.Remaining?.ToString("0.###", _localization.Culture);
        return number is null ? null : $"{name}: {number}";
    }
    private string ErrorText(UsageHistoryErrorKind error)
    {
        var (key, fallback) = error switch
        {
            UsageHistoryErrorKind.Corrupt => ("history.error.corrupt", "Saved history is corrupt and was not changed. Clear it to restart."),
            UsageHistoryErrorKind.TooLarge => ("history.error.tooLarge", "Saved history exceeds the supported size. Clear it to restart."),
            UsageHistoryErrorKind.InvalidData => ("history.error.invalid", "History measurements failed validation. Existing files were not changed."),
            _ => ("history.error.unavailable", "Saved history storage is unavailable."),
        };
        return _localization.GetString(key, fallback);
    }
    private void ClearHistory()
    {
        if (Volatile.Read(ref _disposed) == 0) _controller.Clear();
    }
}
