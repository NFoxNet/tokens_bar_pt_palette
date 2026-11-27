using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Core.Services;

/// <summary>Samples successful cache snapshots while the opt-in history surface is active.</summary>
public sealed class UsageHistoryController : IDisposable
{
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> AllowedMetricKeys = new(StringComparer.Ordinal)
    {
        "tokens5h",
        "tokens7d",
        "balance",
        "totalBalance",
        "quotaUnavailable",
        "connectionStatus",
    };

    private readonly UsageHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageSnapshotCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderHistoryState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageHistoryErrorKind> _readErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageHistoryErrorKind> _writeErrors = new(StringComparer.OrdinalIgnoreCase);
    private UsageHistoryErrorKind? _clearError;
    private bool _disposed;

    public UsageHistoryController(
        UsageHistoryStore store,
        IReadOnlyList<UsageSnapshotCache> caches,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        UpdateProviders(caches ?? throw new ArgumentNullException(nameof(caches)));
    }

    public event EventHandler? Changed;
    public event EventHandler? PaceChanged;

    public UsageHistoryErrorKind? LastErrorKind
    {
        get
        {
            lock (_gate)
            {
                return _clearError
                    ?? _readErrors.Values.Select(error => (UsageHistoryErrorKind?)error).FirstOrDefault()
                    ?? _writeErrors.Values.Select(error => (UsageHistoryErrorKind?)error).FirstOrDefault();
            }
        }
    }

    public void UpdateProviders(IReadOnlyList<UsageSnapshotCache> caches)
    {
        ArgumentNullException.ThrowIfNull(caches);
        var next = new Dictionary<string, UsageSnapshotCache>(StringComparer.OrdinalIgnoreCase);
        foreach (var cache in caches)
        {
            ArgumentNullException.ThrowIfNull(cache);
            if (!next.TryAdd(cache.Descriptor.Id, cache))
            {
                throw new ArgumentException("Provider cache IDs must be unique.", nameof(caches));
            }
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (providerId, previous) in _caches.ToArray())
            {
                if (next.TryGetValue(providerId, out var replacement) && ReferenceEquals(previous, replacement))
                {
                    continue;
                }

                previous.StateChanged -= CacheOnStateChanged;
                _caches.Remove(providerId);
                if (!next.ContainsKey(providerId))
                {
                    _states.Remove(providerId);
                    _readErrors.Remove(providerId);
                    _writeErrors.Remove(providerId);
                }
            }

            foreach (var (providerId, cache) in next)
            {
                if (_caches.TryGetValue(providerId, out var existing) && ReferenceEquals(existing, cache))
                {
                    continue;
                }

                _caches[providerId] = cache;
                _states[providerId] = new ProviderHistoryState(Guid.NewGuid());
                cache.StateChanged += CacheOnStateChanged;
            }
        }

        foreach (var cache in next.Values)
        {
            ProcessCache(cache);
        }
    }

    public void ResetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_states.TryGetValue(providerId, out var state))
            {
                state.SegmentId = Guid.NewGuid();
                state.SourceHash = null;
                state.AccountScopeHash = null;
                state.ObservationHash = null;
                state.PendingEntry = null;
                state.HasIdentity = false;
                state.CurrentEntry = null;
            }
        }
        PaceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Projects saved samples only for the currently observed, confirmed series.</summary>
    public UsagePaceEstimate? EstimatePace(string providerId, IReadOnlyList<UsageHistoryEntry> entries, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            if (_disposed || entries.Count == 0 || _writeErrors.ContainsKey(providerId)
                || !_states.TryGetValue(providerId, out var state) || state.CurrentEntry is not { } current
                || current.IsEstimate || current.SourceKind != UsageHistorySourceKind.Api)
            {
                return null;
            }

            var latest = entries[^1];
            if (latest.ProviderId != providerId || latest.SegmentId != state.SegmentId
                || latest.SourceKind != current.SourceKind || current.Timestamp < latest.Timestamp || now < current.Timestamp)
            {
                return null;
            }

            var estimate = UsagePaceEstimator.Estimate(entries, now);
            if (estimate is null) return null;
            var observedWindow = estimate.IsPrimaryWindow ? current.PrimaryWindow : current.SecondaryWindow;
            var savedWindow = estimate.IsPrimaryWindow ? latest.PrimaryWindow : latest.SecondaryWindow;
            return observedWindow?.IsForecastEligible == true
                && observedWindow.StartsAt == savedWindow!.StartsAt
                && observedWindow.ResetAt == savedWindow.ResetAt
                && observedWindow.LimitWindowSeconds == savedWindow.LimitWindowSeconds
                && double.IsFinite(observedWindow.UsedPercent)
                && observedWindow.UsedPercent >= savedWindow.UsedPercent && observedWindow.UsedPercent <= 100
                ? estimate : null;
        }
    }

    public IReadOnlyList<UsageHistoryEntry> ReadRecent(string providerId, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (limit is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "History page reads at most 20 samples per provider.");
        }

        try
        {
            var entries = _store.Read(providerId)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(limit)
                .ToArray();
            lock (_gate)
            {
                _readErrors.Remove(providerId);
            }
            return entries;
        }
        catch (UsageHistoryException exception)
        {
            SetReadError(providerId, exception.Kind);
            return [];
        }
        catch (IOException)
        {
            SetReadError(providerId, UsageHistoryErrorKind.Unavailable);
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            SetReadError(providerId, UsageHistoryErrorKind.Unavailable);
            return [];
        }
    }

    public bool Clear()
    {
        try
        {
            _store.Clear();
            lock (_gate)
            {
                _clearError = null;
                _readErrors.Clear();
                _writeErrors.Clear();
                foreach (var state in _states.Values)
                {
                    state.SegmentId = Guid.NewGuid();
                    state.PendingEntry = null;
                    state.CurrentEntry = null;
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (IOException)
        {
            SetClearError(UsageHistoryErrorKind.Unavailable);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            SetClearError(UsageHistoryErrorKind.Unavailable);
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var cache in _caches.Values)
            {
                cache.StateChanged -= CacheOnStateChanged;
            }
            _caches.Clear();
            _states.Clear();
            _readErrors.Clear();
            _writeErrors.Clear();
            Changed = null;
            PaceChanged = null;
        }
    }

    private void CacheOnStateChanged(object? sender, EventArgs args)
    {
        if (sender is UsageSnapshotCache cache)
        {
            ProcessCache(cache);
        }
    }

    private void ProcessCache(UsageSnapshotCache cache)
    {
        var providerId = cache.Descriptor.Id;
        // Cache callbacks hold their own state lock; never read another cache under _gate.
        var providerState = cache.State;
        var notify = false;
        var paceChanged = false;
        lock (_gate)
        {
            if (_disposed || !_caches.TryGetValue(providerId, out var registered) || !ReferenceEquals(registered, cache)
                || !_states.TryGetValue(providerId, out var state))
            {
                return;
            }

            if (providerState.ErrorKind != UsageProviderErrorKind.None || providerState.Snapshot is null)
            {
                paceChanged = state.CurrentEntry is not null;
                state.CurrentEntry = null;
                if (providerState.Snapshot is null && state.HasIdentity)
                {
                    state.SegmentId = Guid.NewGuid();
                    state.HasIdentity = false;
                    state.ObservationHash = null;
                    state.PendingEntry = null;
                }
            }
            else if (!providerState.IsRefreshing)
            {
                var snapshot = providerState.Snapshot;
                var timestamp = providerState.LastSuccessfulRefreshAt ?? snapshot.FetchedAt ?? _timeProvider.GetUtcNow();
                var sourceHash = Hash(snapshot.Source);
                var accountScopeHash = Hash(snapshot.AccountScope);
                var sourceKind = SourceKind(snapshot);
                var observationHash = Fingerprint(snapshot, sourceHash, accountScopeHash, timestamp);
                if (state.HasIdentity && (!string.Equals(state.SourceHash, sourceHash, StringComparison.Ordinal)
                    || !string.Equals(state.AccountScopeHash, accountScopeHash, StringComparison.Ordinal)
                    || state.SourceKind != sourceKind))
                {
                    state.SegmentId = Guid.NewGuid();
                    state.ObservationHash = null;
                    state.PendingEntry = null;
                }
                state.SourceHash = sourceHash;
                state.AccountScopeHash = accountScopeHash;
                state.SourceKind = sourceKind;
                state.HasIdentity = true;

                var entry = CreateEntry(snapshot, providerId, timestamp, sourceKind, state.SegmentId);
                paceChanged = state.CurrentEntry is null || !string.Equals(state.ObservationHash, observationHash, StringComparison.Ordinal);
                state.CurrentEntry = entry;
                if (!string.Equals(state.ObservationHash, observationHash, StringComparison.Ordinal) || state.PendingEntry is not null)
                {
                    state.ObservationHash = observationHash;
                    state.PendingEntry = entry;
                    if (state.LastAttemptAt is not { } lastAttempt || timestamp - lastAttempt >= MinimumSampleInterval)
                    {
                        try
                        {
                            var appended = _store.Append(entry);
                            state.LastAttemptAt = timestamp;
                            if (appended)
                            {
                                state.PendingEntry = null;
                                _writeErrors.Remove(providerId);
                                notify = true;
                            }
                        }
                        catch (UsageHistoryException exception)
                        {
                            state.LastAttemptAt = timestamp;
                            SetWriteErrorUnsafe(providerId, exception.Kind);
                            notify = true;
                        }
                        catch (IOException)
                        {
                            state.LastAttemptAt = timestamp;
                            SetWriteErrorUnsafe(providerId, UsageHistoryErrorKind.Unavailable);
                            notify = true;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            state.LastAttemptAt = timestamp;
                            SetWriteErrorUnsafe(providerId, UsageHistoryErrorKind.Unavailable);
                            notify = true;
                        }
                    }
                }
            }
        }

        if (notify) Changed?.Invoke(this, EventArgs.Empty);
        else if (paceChanged) PaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static UsageHistoryEntry CreateEntry(
        UsageSnapshot snapshot,
        string providerId,
        DateTimeOffset timestamp,
        UsageHistorySourceKind sourceKind,
        Guid segmentId)
    {
        static UsageHistoryWindow? Copy(UsageWindow? window)
            => window is null
                ? null
                : new UsageHistoryWindow(window.UsedPercent, window.StartsAt, window.ResetAt, window.LimitWindowSeconds, window.IsFixedWindow);

        var metrics = snapshot.Metrics
            .Where(metric => metric.SemanticKey is not null && AllowedMetricKeys.Contains(metric.SemanticKey))
            .Select(metric => new UsageHistoryMetric(metric.SemanticKey!, metric.NumericValue, metric.Used, metric.Limit, metric.Remaining))
            .ToArray();

        return new UsageHistoryEntry(
            timestamp,
            providerId,
            sourceKind,
            segmentId,
            snapshot.IsEstimate,
            Copy(snapshot.PrimaryWindow),
            Copy(snapshot.SecondaryWindow),
            metrics);
    }

    private static UsageHistorySourceKind SourceKind(UsageSnapshot snapshot)
        => snapshot.IsEstimate ? UsageHistorySourceKind.Local : snapshot.Source is null
            ? UsageHistorySourceKind.Unknown
            : UsageHistorySourceKind.Api;

    private static string? Hash(string? value)
        => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Fingerprint(UsageSnapshot snapshot, string? sourceHash, string? accountScopeHash, DateTimeOffset timestamp)
    {
        var value = new StringBuilder();
        static void Add(StringBuilder builder, string? item)
            => builder.Append(item ?? "<null>").Append('\n');
        static string Number(double? number)
            => number?.ToString("R", CultureInfo.InvariantCulture) ?? "<null>";
        static string DecimalNumber(decimal? number)
            => number?.ToString(CultureInfo.InvariantCulture) ?? "<null>";
        static void AddWindow(StringBuilder builder, UsageWindow? window)
        {
            if (window is null)
            {
                Add(builder, null);
                return;
            }

            Add(builder, Number(window.UsedPercent));
            Add(builder, window.StartsAt?.UtcTicks.ToString(CultureInfo.InvariantCulture));
            Add(builder, window.ResetAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
            Add(builder, window.LimitWindowSeconds.ToString(CultureInfo.InvariantCulture));
            Add(builder, window.IsFixedWindow.ToString());
        }

        Add(value, snapshot.ProviderId);
        Add(value, snapshot.IsEstimate.ToString());
        Add(value, sourceHash);
        Add(value, accountScopeHash);
        Add(value, timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture));
        AddWindow(value, snapshot.PrimaryWindow);
        AddWindow(value, snapshot.SecondaryWindow);
        foreach (var metric in snapshot.Metrics
                     .Where(metric => metric.SemanticKey is not null && AllowedMetricKeys.Contains(metric.SemanticKey))
                     .OrderBy(metric => metric.SemanticKey, StringComparer.Ordinal))
        {
            Add(value, metric.SemanticKey);
            Add(value, DecimalNumber(metric.NumericValue));
            Add(value, Number(metric.Used));
            Add(value, Number(metric.Limit));
            Add(value, Number(metric.Remaining));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private void SetReadError(string providerId, UsageHistoryErrorKind errorKind)
    {
        lock (_gate)
        {
            _readErrors[providerId] = errorKind;
        }
    }

    private void SetClearError(UsageHistoryErrorKind errorKind)
    {
        lock (_gate)
        {
            _clearError = errorKind;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetWriteErrorUnsafe(string providerId, UsageHistoryErrorKind errorKind)
        => _writeErrors[providerId] = errorKind;

    private sealed class ProviderHistoryState(Guid segmentId)
    {
        public Guid SegmentId { get; set; } = segmentId;
        public UsageHistoryEntry? CurrentEntry { get; set; }
        public UsageHistorySourceKind SourceKind { get; set; }
        public string? SourceHash { get; set; }
        public string? AccountScopeHash { get; set; }
        public string? ObservationHash { get; set; }
        public UsageHistoryEntry? PendingEntry { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public bool HasIdentity { get; set; }
    }
}
