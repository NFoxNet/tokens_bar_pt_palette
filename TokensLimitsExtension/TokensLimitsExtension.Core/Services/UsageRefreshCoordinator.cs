namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// Owns the single refresh schedule for all active providers. UI surfaces only
/// consume cache state, which prevents Dock and Command Palette from racing.
/// </summary>
public sealed class UsageRefreshCoordinator : IDisposable
{
    private readonly IUsageRefreshSettings _settings;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Dictionary<string, CancellationTokenSource> _providerTokens = new(StringComparer.OrdinalIgnoreCase);
    private IUsageProviderStateSource[] _providers = [];
    private Timer? _timer;
    private int _disposed;

    public UsageRefreshCoordinator(IUsageRefreshSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Changed += SettingsOnChanged;
        ResetTimer();
    }

    public void UpdateProviders(IEnumerable<IUsageProviderStateSource> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var next = providers.ToArray();
        lock (_gate)
        {
            var nextIds = next.Select(provider => provider.Descriptor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, token) in _providerTokens.Where(pair => !nextIds.Contains(pair.Key)).ToArray())
            {
                token.Cancel();
                token.Dispose();
                _providerTokens.Remove(id);
            }

            _providers = next;
            foreach (var provider in next)
            {
                if (!_providerTokens.ContainsKey(provider.Descriptor.Id))
                {
                    _providerTokens[provider.Descriptor.Id] = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                }
            }
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
            if (!_providerTokens.TryGetValue(provider.Descriptor.Id, out var source))
            {
                return Task.CompletedTask;
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
            foreach (var token in _providerTokens.Values)
            {
                token.Dispose();
            }
            _providerTokens.Clear();
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

    private static async Task RefreshProviderSafelyAsync(
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
    }

    private void SettingsOnChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ResetTimer();
        }
    }

    private void ResetTimer()
    {
        var dueTime = _settings.RefreshInterval > TimeSpan.Zero ? _settings.RefreshInterval : TimeSpan.FromMinutes(1);
        lock (_gate)
        {
            _timer ??= new Timer(_ => RefreshAll(), null, dueTime, dueTime);
            _timer.Change(dueTime, dueTime);
        }
    }
}
