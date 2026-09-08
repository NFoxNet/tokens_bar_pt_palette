using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// Shares one refresh task and one last-known snapshot between all UI surfaces
/// for a provider. This prevents the Dock and details page from issuing
/// duplicate requests at the same time.
/// </summary>
public sealed class UsageSnapshotCache : IUsageProviderStateSource, IRefreshCancellationSource, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly IUsageProviderConfigurationChangeSource? _configurationChanges;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _stateGate = new();
    private CancellationTokenSource _generationCts = new();
    private UsageSnapshot? _snapshot;
    private DateTimeOffset _fetchedAt;
    private UsageProviderState _state = new(null, null, null, false);
    private Task<UsageSnapshot>? _inFlightRefresh;
    private long _invalidationVersion;
    private long _configurationGeneration;
    private int _disposed;

    public UsageSnapshotCache(
        IUsageProvider provider,
        IUsageRefreshSettings? refreshSettings = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _refreshSettings = refreshSettings;
        _configurationChanges = refreshSettings as IUsageProviderConfigurationChangeSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_configurationChanges is not null)
        {
            _configurationChanges.ProviderConfigurationChanged += ProviderConfigurationOnChanged;
        }
        else
        {
            _refreshSettings?.Changed += RefreshSettingsOnChanged;
        }
    }

    public UsageProviderDescriptor Descriptor => _provider.Descriptor;

    public event EventHandler? StateChanged;

    public UsageProviderState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public DateTimeOffset? LastFetchedAt
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot is null ? null : _fetchedAt;
            }
        }
    }

    public bool TryGetSnapshot(out UsageSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (_snapshot is null)
            {
                snapshot = null!;
                return false;
            }

            snapshot = _snapshot;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_stateGate)
        {
            _fetchedAt = default;
            _invalidationVersion++;
        }
    }

    /// <summary>Invalidates values that belong to a previous credential/account.</summary>
    public void Clear()
    {
        CancellationTokenSource previousGeneration;
        lock (_stateGate)
        {
            _snapshot = null;
            _fetchedAt = default;
            _invalidationVersion++;
            _configurationGeneration++;
            previousGeneration = _generationCts;
            _generationCts = new CancellationTokenSource();
            _inFlightRefresh = null;
            _state = _state with
            {
                Snapshot = null,
                LastSuccessfulRefreshAt = null,
                IsRefreshing = false,
                ErrorKind = UsageProviderErrorKind.None,
                RetryAfter = null,
            };
        }
        previousGeneration.Cancel();
        RaiseStateChanged();
    }

    void IRefreshCancellationSource.CancelRefreshForDeactivation()
    {
        CancellationTokenSource previousGeneration;
        lock (_stateGate)
        {
            previousGeneration = _generationCts;
            _generationCts = new CancellationTokenSource();
            _inFlightRefresh = null;
            _invalidationVersion++;
            _configurationGeneration++;
            _state = _state with { IsRefreshing = false, ErrorKind = UsageProviderErrorKind.None, RetryAfter = null };
        }
        previousGeneration.Cancel();
        RaiseStateChanged();
    }

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetFreshSnapshot(out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        Task<UsageSnapshot> refreshTask;
        lock (_stateGate)
        {
            if (TryGetFreshSnapshotUnsafe(out cachedSnapshot))
            {
                return cachedSnapshot;
            }

            if (_inFlightRefresh is null)
            {
                refreshTask = FetchSnapshotAsync(_generationCts.Token);
                _inFlightRefresh = refreshTask;
                _ = refreshTask.ContinueWith(
                    _ => ClearInFlightRefresh(refreshTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                refreshTask = _inFlightRefresh;
            }
        }

        // A page that goes away stops waiting without cancelling the provider
        // request shared with the Dock and other pages.
        return await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot> FetchSnapshotAsync(CancellationToken generationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            generationToken,
            _lifetimeCts.Token);
        while (true)
        {
            ThrowIfDisposed();
            linkedCts.Token.ThrowIfCancellationRequested();

            long requestVersion;
            lock (_stateGate)
            {
                requestVersion = _invalidationVersion;
            }

            var freshSnapshot = await _provider
                .GetUsageSnapshotAsync(linkedCts.Token)
                .ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var fetchedAt = _timeProvider.GetUtcNow();
            freshSnapshot = freshSnapshot with { FetchedAt = fetchedAt };

            lock (_stateGate)
            {
                if (requestVersion != _invalidationVersion)
                {
                    // Settings changed while the request was in flight. Do not
                    // let the stale response become the fresh cache entry.
                    continue;
                }

                _snapshot = freshSnapshot;
                _fetchedAt = fetchedAt;
                _state = new UsageProviderState(freshSnapshot, fetchedAt, fetchedAt, false);
            }
            RaiseStateChanged();
            return freshSnapshot;
        }
    }

    private void ClearInFlightRefresh(Task<UsageSnapshot> completedTask)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_inFlightRefresh, completedTask))
            {
                _inFlightRefresh = null;
            }
        }
    }

    private long GetConfigurationGeneration()
    {
        lock (_stateGate)
        {
            return _configurationGeneration;
        }
    }

    private bool HasConfigurationGenerationChanged(long generation)
    {
        lock (_stateGate)
        {
            return generation != _configurationGeneration;
        }
    }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (force)
        {
            InvalidateUnlessRefreshIsAlreadyInFlight();
        }
        else if (TryGetFreshSnapshot(out _))
        {
            return;
        }

        var configurationGeneration = GetConfigurationGeneration();
        UpdateState(isRefreshing: true, errorKind: UsageProviderErrorKind.None, retryAfter: null);
        try
        {
            await GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false);
            UpdateState(isRefreshing: false, errorKind: UsageProviderErrorKind.None, retryAfter: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
        {
            UpdateState(isRefreshing: false, errorKind: UsageProviderErrorKind.None, retryAfter: null);
            throw;
        }
        catch (OperationCanceledException) when (HasConfigurationGenerationChanged(configurationGeneration))
        {
            // Clear already published the state for the new configuration. An
            // old cancelled operation must not replace it with an error.
        }
        catch (Exception exception)
        {
            UpdateState(
                isRefreshing: false,
                errorKind: UsageProviderErrorClassifier.Classify(exception),
                retryAfter: UsageProviderErrorClassifier.GetRetryAfter(exception));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_configurationChanges is not null)
        {
            _configurationChanges.ProviderConfigurationChanged -= ProviderConfigurationOnChanged;
        }
        else
        {
            _refreshSettings?.Changed -= RefreshSettingsOnChanged;
        }
        _lifetimeCts.Cancel();
        _generationCts.Cancel();
        GC.SuppressFinalize(this);
    }

    private bool TryGetFreshSnapshot(out UsageSnapshot snapshot)
    {
        lock (_stateGate)
        {
            return TryGetFreshSnapshotUnsafe(out snapshot);
        }
    }

    private bool TryGetFreshSnapshotUnsafe(out UsageSnapshot snapshot)
    {
        if (_snapshot is not null
            && _fetchedAt != default
            && _timeProvider.GetUtcNow() - _fetchedAt < GetRefreshInterval())
        {
            snapshot = _snapshot;
            return true;
        }

        snapshot = null!;
        return false;
    }

    private TimeSpan GetRefreshInterval()
    {
        var interval = _refreshSettings?.RefreshInterval ?? TimeSpan.FromMinutes(1);
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(1);
    }

    private void RefreshSettingsOnChanged(object? sender, EventArgs e)
    {
        Invalidate();
    }

    private void InvalidateUnlessRefreshIsAlreadyInFlight()
    {
        lock (_stateGate)
        {
            if (_inFlightRefresh is not null)
            {
                return;
            }

            _fetchedAt = default;
            _invalidationVersion++;
        }
    }

    private void ProviderConfigurationOnChanged(object? sender, EventArgs e) => Clear();

    private void UpdateState(bool isRefreshing, UsageProviderErrorKind errorKind, TimeSpan? retryAfter)
    {
        lock (_stateGate)
        {
            _state = _state with
            {
                Snapshot = _snapshot,
                LastSuccessfulRefreshAt = _snapshot is null ? null : _fetchedAt,
                LastAttemptAt = isRefreshing ? _timeProvider.GetUtcNow() : _state.LastAttemptAt,
                IsRefreshing = isRefreshing,
                ErrorKind = errorKind,
                RetryAfter = retryAfter,
            };
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
