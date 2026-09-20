using System.Diagnostics;

namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// Owns the single refresh schedule for all active providers. UI surfaces only
/// consume cache state, which prevents Dock and Command Palette from racing.
/// </summary>
public sealed class UsageRefreshCoordinator : IDisposable
{
    private readonly IUsageRefreshSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitterSource;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<string, CancellationTokenSource> _providerTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextRefreshAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _transientFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private IUsageProviderStateSource[] _providers = [];
    private TimeSpan _refreshInterval;
    private ITimer? _timer;
    private int _disposed;

    public UsageRefreshCoordinator(
        IUsageRefreshSettings settings,
        TimeProvider? timeProvider = null,
        Func<double>? jitterSource = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterSource = jitterSource ?? Random.Shared.NextDouble;
        _refreshInterval = GetRefreshInterval();
        _settings.Changed += SettingsOnChanged;
    }

    public void UpdateProviders(IEnumerable<IUsageProviderStateSource> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var next = providers.ToArray();
        IRefreshCancellationSource[] removedRefreshes;
        lock (_gate)
        {
            var nextIds = next.Select(provider => provider.Descriptor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            removedRefreshes = _providers
                .Where(provider => !next.Any(current => ReferenceEquals(current, provider)))
                .OfType<IRefreshCancellationSource>()
                .ToArray();
            foreach (var (id, token) in _providerTokens.Where(pair => !nextIds.Contains(pair.Key)).ToArray())
            {
                token.Cancel();
                token.Dispose();
                _providerTokens.Remove(id);
                _nextRefreshAt.Remove(id);
                _cooldownUntil.Remove(id);
                _transientFailureCounts.Remove(id);
            }

            _providers = next;
            foreach (var provider in next)
            {
                if (!_providerTokens.ContainsKey(provider.Descriptor.Id))
                {
                    _providerTokens[provider.Descriptor.Id] = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    _nextRefreshAt[provider.Descriptor.Id] = _timeProvider.GetUtcNow();
                }
            }

            if (next.Length == 0)
            {
                ScheduleNearestRefreshUnsafe();
            }
        }

        foreach (var refresh in removedRefreshes)
        {
            refresh.CancelRefreshForDeactivation();
        }

        RefreshAll();
    }

    public void RefreshAll(bool force = false)
    {
        foreach (var provider in SnapshotProviders())
        {
            _ = RefreshProviderAsync(provider, force);
        }
    }

    public Task RefreshProviderAsync(IUsageProviderStateSource provider, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        CancellationToken token;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || !IsCurrentProviderUnsafe(provider)
                || !_providerTokens.TryGetValue(provider.Descriptor.Id, out var source))
            {
                return Task.CompletedTask;
            }

            if (force
                && _cooldownUntil.TryGetValue(provider.Descriptor.Id, out var cooldownUntil))
            {
                if (cooldownUntil > _timeProvider.GetUtcNow())
                {
                    return Task.CompletedTask;
                }

                _cooldownUntil.Remove(provider.Descriptor.Id);
            }

            token = source.Token;
        }

        return RefreshProviderSafelyAsync(provider, force, token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settings.Changed -= SettingsOnChanged;
        _lifetimeCts.Cancel();
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            foreach (var token in _providerTokens.Values)
            {
                token.Dispose();
            }
            _providerTokens.Clear();
            _nextRefreshAt.Clear();
            _cooldownUntil.Clear();
            _transientFailureCounts.Clear();
            _providers = [];
        }
        _lifetimeCts.Dispose();
    }

    private IUsageProviderStateSource[] SnapshotProviders()
    {
        lock (_gate)
        {
            return _providers.ToArray();
        }
    }

    private async Task RefreshProviderSafelyAsync(
        IUsageProviderStateSource provider,
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            await provider.RefreshAsync(force, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disabling a provider cancels its in-flight request by design.
        }
        catch (Exception exception)
        {
            // RefreshAll is intentionally fire-and-forget. Observe an unexpected
            // provider failure here so it cannot become an unobserved task fault;
            // the cache normally classifies its own provider failures.
            Debug.WriteLine($"[TokensLimits] coordinator refresh failed for {provider.Descriptor.Id}: {exception.GetType().Name}");
        }
        finally
        {
            ScheduleAfterRefresh(provider);
        }
    }

    private void SettingsOnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ResetScheduleForChangedInterval();
        }
    }

    private void ScheduleAfterRefresh(IUsageProviderStateSource provider)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsCurrentProviderUnsafe(provider))
            {
                return;
            }

            if (_providerTokens.ContainsKey(provider.Descriptor.Id))
            {
                var nextRefreshAt = GetNextRefreshAtUnsafe(provider);
                _nextRefreshAt[provider.Descriptor.Id] = nextRefreshAt;
                if (IsCooldownError(provider.State.ErrorKind)
                    && nextRefreshAt != DateTimeOffset.MaxValue)
                {
                    _cooldownUntil[provider.Descriptor.Id] = nextRefreshAt;
                }
                else
                {
                    _cooldownUntil.Remove(provider.Descriptor.Id);
                }
            }
            ScheduleNearestRefreshUnsafe();
        }
    }

    private bool IsCurrentProviderUnsafe(IUsageProviderStateSource provider)
        => _providers.Any(current => ReferenceEquals(current, provider));

    private DateTimeOffset GetNextRefreshAtUnsafe(IUsageProviderStateSource provider)
    {
        var now = _timeProvider.GetUtcNow();
        var state = provider.State;
        if (state.ErrorKind == UsageProviderErrorKind.None)
        {
            _transientFailureCounts.Remove(provider.Descriptor.Id);
            return now + _refreshInterval;
        }

        if (state.ErrorKind == UsageProviderErrorKind.MissingConfiguration)
        {
            _transientFailureCounts.Remove(provider.Descriptor.Id);
            return DateTimeOffset.MaxValue;
        }

        if (state.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            _transientFailureCounts.Remove(provider.Descriptor.Id);
            return now + retryAfter;
        }

        if (state.ErrorKind is UsageProviderErrorKind.Network or UsageProviderErrorKind.Timeout or UsageProviderErrorKind.RateLimited)
        {
            var failures = _transientFailureCounts.TryGetValue(provider.Descriptor.Id, out var previousFailures)
                ? previousFailures + 1
                : 1;
            _transientFailureCounts[provider.Descriptor.Id] = failures;
            var backoffSeconds = Math.Min(60, 5 * Math.Pow(2, failures - 1));
            var jitter = _jitterSource();
            if (double.IsNaN(jitter) || double.IsInfinity(jitter))
            {
                jitter = 0.5;
            }

            jitter = Math.Clamp(jitter, 0, 1);
            return now + TimeSpan.FromSeconds(backoffSeconds * (0.5 + jitter));
        }

        _transientFailureCounts.Remove(provider.Descriptor.Id);
        return now + _refreshInterval;
    }

    private void ResetScheduleForChangedInterval()
    {
        var refreshInterval = GetRefreshInterval();
        lock (_gate)
        {
            if (refreshInterval == _refreshInterval)
            {
                return;
            }

            _refreshInterval = refreshInterval;
            var now = _timeProvider.GetUtcNow();
            foreach (var provider in _providers)
            {
                var lastSuccess = provider.State.LastSuccessfulRefreshAt;
                _nextRefreshAt[provider.Descriptor.Id] = lastSuccess is null
                    ? now
                    : Max(now, lastSuccess.Value + _refreshInterval);
            }
            ScheduleNearestRefreshUnsafe();
        }
    }

    private void ScheduleNearestRefreshUnsafe()
    {
        var scheduledRefreshes = _nextRefreshAt.Values
            .Where(nextRefresh => nextRefresh != DateTimeOffset.MaxValue)
            .ToArray();
        if (scheduledRefreshes.Length == 0)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var nextRefresh = scheduledRefreshes.Min();
        var dueTime = nextRefresh > now ? nextRefresh - now : TimeSpan.Zero;
        _timer ??= _timeProvider.CreateTimer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerTick(object? state)
    {
        IUsageProviderStateSource[] dueProviders;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            dueProviders = _providers
                .Where(provider => _nextRefreshAt.TryGetValue(provider.Descriptor.Id, out var dueAt) && dueAt <= now)
                .ToArray();
            foreach (var provider in dueProviders)
            {
                // Prevent the single timer from repeatedly dispatching a
                // request while this provider is still refreshing.
                _nextRefreshAt[provider.Descriptor.Id] = DateTimeOffset.MaxValue;
            }
            ScheduleNearestRefreshUnsafe();
        }

        foreach (var provider in dueProviders)
        {
            _ = RefreshProviderAsync(provider);
        }
    }

    private TimeSpan GetRefreshInterval()
    {
        var interval = _settings.RefreshInterval;
        return interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(1);
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
        => first >= second ? first : second;

    private static bool IsCooldownError(UsageProviderErrorKind errorKind)
        => errorKind is UsageProviderErrorKind.Network
            or UsageProviderErrorKind.Timeout
            or UsageProviderErrorKind.RateLimited;
}
