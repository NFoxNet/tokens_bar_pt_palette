using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Notifications;

public interface IUsageNotificationDelivery
{
    void Deliver(UsageNotificationEvent notification);
}

public sealed class CommandPaletteUsageNotificationDelivery(ILocalizationService localization) : IUsageNotificationDelivery
{
    public void Deliver(UsageNotificationEvent notification)
    {
        var window = localization.GetString(notification.Window == UsageNotificationWindow.Primary
            ? "notifications.window.primary"
            : "notifications.window.secondary");
        var message = notification.Kind switch
        {
            UsageNotificationEventKind.ThresholdReached => localization.Format(
                "notifications.thresholdMessage",
                notification.ProviderDisplayName,
                window,
                notification.RemainingPercent),
            UsageNotificationEventKind.WindowReset => localization.Format(
                "notifications.resetMessage",
                notification.ProviderDisplayName,
                window),
            _ => throw new ArgumentOutOfRangeException(nameof(notification)),
        };

        new ToastStatusMessage(message).Show();
    }
}

/// <summary>Observes successful cache updates while notifications are enabled.</summary>
public sealed partial class UsageNotificationController : IDisposable
{
    public const double DefaultThresholdPercent = 10;

    private readonly object _gate = new();
    private readonly UsageNotificationEvaluator _evaluator;
    private readonly IUsageNotificationDelivery _delivery;
    private readonly Func<string, string> _getSegmentId;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastEvaluatedAt = new(StringComparer.Ordinal);
    private double _thresholdPercent;
    private int _disposed;

    public UsageNotificationController(
        IReadOnlyList<UsageSnapshotCache> caches,
        UsageNotificationEvaluator evaluator,
        IUsageNotificationDelivery delivery,
        Func<string, string> getSegmentId,
        bool enabled = true,
        double thresholdPercent = DefaultThresholdPercent,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(caches);
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _getSegmentId = getSegmentId ?? throw new ArgumentNullException(nameof(getSegmentId));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ThresholdPercent = thresholdPercent;
        IsEnabled = enabled;
        if (enabled)
        {
            UpdateProviders(caches);
        }
    }

    public bool IsEnabled { get; }

    public double ThresholdPercent
    {
        get => _thresholdPercent;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _thresholdPercent = value;
        }
    }

    public event EventHandler<UsageNotificationDeliveryFailedEventArgs>? DeliveryFailed;

    public void UpdateProviders(IReadOnlyList<UsageSnapshotCache> caches)
    {
        ArgumentNullException.ThrowIfNull(caches);
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var next = caches.ToDictionary(cache => cache.Descriptor.Id, StringComparer.Ordinal);
        List<Subscription> removed = [];
        lock (_gate)
        {
            foreach (var (providerId, subscription) in _subscriptions.ToArray())
            {
                if (next.TryGetValue(providerId, out var cache) && ReferenceEquals(cache, subscription.Cache))
                {
                    next.Remove(providerId);
                    continue;
                }

                _subscriptions.Remove(providerId);
                _lastEvaluatedAt.Remove(providerId);
                _evaluator.ResetProvider(providerId);
                removed.Add(subscription);
            }

            foreach (var (providerId, cache) in next)
            {
                EventHandler handler = (_, _) => CacheOnStateChanged(providerId, cache);
                _subscriptions.Add(providerId, new Subscription(cache, handler));
                cache.StateChanged += handler;
            }
        }

        foreach (var subscription in removed)
        {
            subscription.Cache.StateChanged -= subscription.Handler;
        }
    }

    /// <summary>Call when credentials, account, provider source, or configuration changes.</summary>
    public void ResetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_gate)
        {
            _evaluator.ResetProvider(providerId);
            _lastEvaluatedAt.Remove(providerId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
            _lastEvaluatedAt.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Cache.StateChanged -= subscription.Handler;
        }
    }

    private void CacheOnStateChanged(string providerId, UsageSnapshotCache cache)
    {
        IReadOnlyList<UsageNotificationEvent> notifications;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0
                || !_subscriptions.TryGetValue(providerId, out var current)
                || !ReferenceEquals(current.Cache, cache))
            {
                return;
            }

            var state = cache.State;
            if (state.IsRefreshing
                || state.ErrorKind != UsageProviderErrorKind.None
                || state.Snapshot is not { IsEstimate: false, FetchedAt: { } fetchedAt } snapshot
                || state.LastSuccessfulRefreshAt != fetchedAt
                || fetchedAt > _timeProvider.GetUtcNow()
                || (_lastEvaluatedAt.TryGetValue(providerId, out var previousFetch) && fetchedAt <= previousFetch))
            {
                return;
            }

            _lastEvaluatedAt[providerId] = fetchedAt;
            notifications = _evaluator.Evaluate(
                _getSegmentId(providerId),
                snapshot,
                _timeProvider.GetUtcNow(),
                ThresholdPercent);
        }

        foreach (var notification in notifications)
        {
            try
            {
                _delivery.Deliver(notification);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[TokensLimits] usage notification delivery failed for {providerId}: {exception.GetType().Name}");
                try
                {
                    DeliveryFailed?.Invoke(this, new UsageNotificationDeliveryFailedEventArgs(providerId, exception.GetType().Name));
                }
                catch (Exception diagnosticException)
                {
                    Debug.WriteLine($"[TokensLimits] notification failure observer failed: {diagnosticException.GetType().Name}");
                }
            }
        }
    }

    private sealed record Subscription(UsageSnapshotCache Cache, EventHandler Handler);
}

public sealed class UsageNotificationDeliveryFailedEventArgs(string providerId, string errorType) : EventArgs
{
    public string ProviderId { get; } = providerId;

    public string ErrorType { get; } = errorType;
}
