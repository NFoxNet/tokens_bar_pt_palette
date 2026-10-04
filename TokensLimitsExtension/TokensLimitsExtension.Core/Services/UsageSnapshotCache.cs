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
    private InFlightOperationKind _inFlightOperationKind;
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

    public bool SupportsConnectionValidation
        => _provider is IUsageProviderConnectionValidator { SupportsConnectionValidation: true };

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
            _inFlightOperationKind = InFlightOperationKind.None;
            _state = _state with
            {
                Snapshot = null,
                LastSuccessfulRefreshAt = null,
                IsRefreshing = false,
                ErrorKind = UsageProviderErrorKind.None,
                RetryAfter = null,
                RetryAfterUntil = null,
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
            _inFlightOperationKind = InFlightOperationKind.None;
            _invalidationVersion++;
            _configurationGeneration++;
            _state = _state with { IsRefreshing = false, ErrorKind = UsageProviderErrorKind.None, RetryAfter = null, RetryAfterUntil = null };
        }
        previousGeneration.Cancel();
        RaiseStateChanged();
    }

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        Task<UsageSnapshot> refreshTask;
        lock (_stateGate)
        {
            if (_inFlightRefresh is not null)
            {
                refreshTask = _inFlightRefresh;
            }
            else if (TryGetFreshSnapshotUnsafe(out var cachedSnapshot))
            {
                return cachedSnapshot;
            }
            else
            {
                refreshTask = StartSharedOperationUnsafe(FetchSnapshotAsync(_generationCts.Token), InFlightOperationKind.Refresh);
            }
        }

        // A page that goes away stops waiting without cancelling the provider
        // request shared with the Dock and other pages.
        return await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsConnectionValidation)
        {
            throw new InvalidOperationException($"Provider '{Descriptor.Id}' does not support connection validation.");
        }

        Task<UsageSnapshot> validationTask;
        lock (_stateGate)
        {
            if (_state.RetryAfterUntil is { } retryAfterUntil && retryAfterUntil > _timeProvider.GetUtcNow())
            {
                return;
            }

            if (_inFlightOperationKind == InFlightOperationKind.Validation && _inFlightRefresh is not null)
            {
                validationTask = _inFlightRefresh;
            }
            else
            {
                var previousRefresh = _inFlightRefresh;
                var configurationGeneration = _configurationGeneration;
                validationTask = RunConnectionValidationAsync(previousRefresh, configurationGeneration, _generationCts.Token);
                StartSharedOperationUnsafe(validationTask, InFlightOperationKind.Validation);
                SetRefreshingStateUnsafe();
            }
        }

        RaiseStateChanged();
        await validationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<UsageSnapshot> RunConnectionValidationAsync(
        Task<UsageSnapshot>? previousRefresh,
        long configurationGeneration,
        CancellationToken generationToken)
    {
        await Task.Yield();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(generationToken, _lifetimeCts.Token);
        BeginSharedOperationState(configurationGeneration, linkedCts.Token);
        try
        {
            if (previousRefresh is not null)
            {
                await previousRefresh.ConfigureAwait(false);
            }

            linkedCts.Token.ThrowIfCancellationRequested();
            var validator = (IUsageProviderConnectionValidator)_provider;
            var validatedSnapshot = await validator.ValidateConnectionAsync(linkedCts.Token).ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var fetchedAt = _timeProvider.GetUtcNow();
            validatedSnapshot = validatedSnapshot with { FetchedAt = fetchedAt };
            lock (_stateGate)
            {
                EnsureConfigurationGenerationUnsafe(configurationGeneration, linkedCts.Token);
                _snapshot = validatedSnapshot;
                _fetchedAt = fetchedAt;
                _state = new UsageProviderState(validatedSnapshot, fetchedAt, fetchedAt, false);
            }
            RaiseStateChanged();
            return validatedSnapshot;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                EnsureConfigurationGenerationUnsafe(configurationGeneration, linkedCts.Token);
                SetFailureStateUnsafe(exception);
            }
            RaiseStateChanged();
            throw;
        }
    }

    private Task<UsageSnapshot> StartSharedOperationUnsafe(
        Task<UsageSnapshot> operation,
        InFlightOperationKind kind)
    {
        _inFlightOperationKind = kind;
        _inFlightRefresh = operation;
        _ = operation.ContinueWith(
            _ => ClearInFlightRefresh(operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    private void SetRefreshingStateUnsafe()
    {
        _state = _state with
        {
            IsRefreshing = true,
            LastAttemptAt = _timeProvider.GetUtcNow(),
            ErrorKind = UsageProviderErrorKind.None,
            RetryAfter = null,
            RetryAfterUntil = null,
        };
    }

    private void BeginSharedOperationState(long configurationGeneration, CancellationToken token)
    {
        lock (_stateGate)
        {
            EnsureConfigurationGenerationUnsafe(configurationGeneration, token);
            ThrowIfDisposed();
            SetRefreshingStateUnsafe();
        }
        RaiseStateChanged();
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
                _state = new UsageProviderState(
                    freshSnapshot,
                    fetchedAt,
                    fetchedAt,
                    _inFlightOperationKind == InFlightOperationKind.Validation);
            }
            RaiseStateChanged();
            return freshSnapshot;
        }
    }

    private void ClearInFlightRefresh(Task<UsageSnapshot> completedTask)
    {
        var failure = completedTask.IsFaulted
            ? completedTask.Exception?.Flatten().InnerExceptions.FirstOrDefault()
            : null;
        var stateChanged = false;
        lock (_stateGate)
        {
            if (ReferenceEquals(_inFlightRefresh, completedTask))
            {
                _inFlightRefresh = null;
                _inFlightOperationKind = InFlightOperationKind.None;
                if (failure is not null && _state.IsRefreshing)
                {
                    SetFailureStateUnsafe(failure);
                    stateChanged = true;
                }
            }
        }

        if (stateChanged)
        {
            RaiseStateChanged();
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
        else if (TryGetFreshSnapshotWhenIdle())
        {
            return;
        }

        var configurationGeneration = GetConfigurationGeneration();
        BeginRefresh();
        try
        {
            await GetUsageSnapshotAsync(cancellationToken).ConfigureAwait(false);
            UpdateState(isRefreshing: false, errorKind: UsageProviderErrorKind.None, retryAfter: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
        {
            if (!HasInFlightOperation())
            {
                var state = State;
                UpdateState(isRefreshing: false, errorKind: state.ErrorKind, retryAfter: state.RetryAfter, preserveRetryAfterUntil: true);
            }
            throw;
        }
        catch (OperationCanceledException) when (HasConfigurationGenerationChanged(configurationGeneration))
        {
            // Clear already published the state for the new configuration. An
            // old cancelled operation must not replace it with an error.
        }
        catch (Exception exception)
        {
            PublishRefreshFailureIfStillRefreshing(exception, configurationGeneration);
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

    private bool HasInFlightOperation()
    {
        lock (_stateGate)
        {
            return _inFlightRefresh is not null;
        }
    }

    private bool TryGetFreshSnapshotWhenIdle()
    {
        lock (_stateGate)
        {
            return _inFlightRefresh is null && TryGetFreshSnapshotUnsafe(out _);
        }
    }

    private void EnsureConfigurationGenerationUnsafe(long generation, CancellationToken token)
    {
        if (generation != _configurationGeneration)
        {
            throw new OperationCanceledException("Provider configuration changed during connection validation.", token);
        }

        token.ThrowIfCancellationRequested();
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

    private void ProviderConfigurationOnChanged(object? sender, UsageProviderConfigurationChangedEventArgs e)
    {
        if (e.ProviderIds.Contains(Descriptor.Id))
        {
            Clear();
        }
    }

    private void UpdateState(
        bool isRefreshing,
        UsageProviderErrorKind errorKind,
        TimeSpan? retryAfter,
        bool preserveRetryAfterUntil = false)
    {
        lock (_stateGate)
        {
            if (!isRefreshing && _inFlightRefresh is { IsCompleted: false })
            {
                isRefreshing = true;
            }

            _state = _state with
            {
                Snapshot = _snapshot,
                LastSuccessfulRefreshAt = _snapshot is null ? null : _state.LastSuccessfulRefreshAt,
                LastAttemptAt = isRefreshing ? _timeProvider.GetUtcNow() : _state.LastAttemptAt,
                IsRefreshing = isRefreshing,
                ErrorKind = errorKind,
                RetryAfter = retryAfter,
                RetryAfterUntil = preserveRetryAfterUntil
                    ? _state.RetryAfterUntil
                    : retryAfter is { } duration && duration > TimeSpan.Zero
                        ? _timeProvider.GetUtcNow() + duration
                        : null,
            };
        }

        RaiseStateChanged();
    }

    private void PublishRefreshFailureIfStillRefreshing(Exception exception, long configurationGeneration)
    {
        lock (_stateGate)
        {
            if (configurationGeneration != _configurationGeneration
                || !_state.IsRefreshing
                || _inFlightOperationKind == InFlightOperationKind.Validation)
            {
                return;
            }

            SetFailureStateUnsafe(exception);
        }

        RaiseStateChanged();
    }

    private void SetFailureStateUnsafe(Exception exception)
    {
        var retryAfter = UsageProviderErrorClassifier.GetRetryAfter(exception);
        _state = _state with
        {
            Snapshot = _snapshot,
            IsRefreshing = false,
            ErrorKind = UsageProviderErrorClassifier.Classify(exception),
            RetryAfter = retryAfter,
            RetryAfterUntil = retryAfter is { } duration && duration > TimeSpan.Zero
                ? _timeProvider.GetUtcNow() + duration
                : null,
        };
    }

    private void BeginRefresh()
    {
        lock (_stateGate)
        {
            var hasLastKnownSnapshot = _snapshot is not null;
            _state = _state with
            {
                IsRefreshing = true,
                LastAttemptAt = _timeProvider.GetUtcNow(),
                ErrorKind = hasLastKnownSnapshot ? _state.ErrorKind : UsageProviderErrorKind.None,
                RetryAfter = hasLastKnownSnapshot ? _state.RetryAfter : null,
                RetryAfterUntil = hasLastKnownSnapshot ? _state.RetryAfterUntil : null,
            };
        }

        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private enum InFlightOperationKind
    {
        None,
        Refresh,
        Validation,
    }
}
