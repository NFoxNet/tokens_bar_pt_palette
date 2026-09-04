using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// Shares one refresh task and one last-known snapshot between all UI surfaces
/// for a provider. This prevents the Dock and details page from issuing
/// duplicate requests at the same time.
/// </summary>
public sealed class UsageSnapshotCache : IUsageProviderStateSource, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly IUsageProviderConfigurationChangeSource? _configurationChanges;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _stateGate = new();
    private UsageSnapshot? _snapshot;
    private DateTimeOffset _fetchedAt;
    private UsageProviderState _state = new(null, null, null, false);
    private long _invalidationVersion;
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
        lock (_stateGate)
        {
            _snapshot = null;
            _fetchedAt = default;
            _invalidationVersion++;
            _state = _state with
            {
                Snapshot = null,
                LastSuccessfulRefreshAt = null,
                ErrorKind = UsageProviderErrorKind.None,
                RetryAfter = null,
            };
        }
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var token = linkedCts.Token;
        while (true)
        {
            await _refreshGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (TryGetFreshSnapshot(out cachedSnapshot))
                {
                    return cachedSnapshot;
                }

                long requestVersion;
                lock (_stateGate)
                {
                    requestVersion = _invalidationVersion;
                }

                var freshSnapshot = await _provider
                    .GetUsageSnapshotAsync(token)
                    .ConfigureAwait(false);
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
            finally
            {
                _refreshGate.Release();
            }
        }
    }

    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (force)
        {
            Invalidate();
        }
        else if (TryGetFreshSnapshot(out _))
        {
            return;
        }

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
        // Do not dispose the gate here: a refresh that already passed WaitAsync
        // still has to execute its finally block and release it. The cache is
        // owned by the extension lifetime, so the managed semaphore can be
        // reclaimed together with the cache after that task completes.
        GC.SuppressFinalize(this);
    }

    private bool TryGetFreshSnapshot(out UsageSnapshot snapshot)
    {
        lock (_stateGate)
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
