using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// Shares one refresh task and one last-known snapshot between all UI surfaces
/// for a provider. This prevents the Dock and details page from issuing
/// duplicate requests at the same time.
/// </summary>
public sealed class UsageSnapshotCache : IRefreshableUsageProvider, IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly IUsageRefreshSettings? _refreshSettings;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _stateGate = new();
    private UsageSnapshot? _snapshot;
    private DateTimeOffset _fetchedAt;
    private long _invalidationVersion;
    private int _disposed;

    public UsageSnapshotCache(
        IUsageProvider provider,
        IUsageRefreshSettings? refreshSettings = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _refreshSettings = refreshSettings;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshSettings?.Changed += RefreshSettingsOnChanged;
    }

    public UsageProviderDescriptor Descriptor => _provider.Descriptor;

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
                }

                return freshSnapshot;
            }
            finally
            {
                _refreshGate.Release();
            }
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
