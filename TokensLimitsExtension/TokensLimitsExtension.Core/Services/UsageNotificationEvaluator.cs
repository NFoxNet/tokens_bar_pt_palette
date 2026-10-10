using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public enum UsageNotificationEventKind
{
    ThresholdReached,
    WindowReset,
}

public enum UsageNotificationWindow
{
    Primary,
    Secondary,
}

public sealed record UsageNotificationEvent(
    string ProviderId,
    string ProviderDisplayName,
    UsageNotificationWindow Window,
    UsageNotificationEventKind Kind,
    double RemainingPercent);

/// <summary>Tracks threshold crossings for one running extension process.</summary>
public sealed class UsageNotificationEvaluator
{
    private readonly Dictionary<WindowKey, WindowState> _windows = [];

    public IReadOnlyList<UsageNotificationEvent> Evaluate(
        string segmentId,
        UsageSnapshot snapshot,
        DateTimeOffset now,
        double thresholdPercent = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(thresholdPercent) || thresholdPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdPercent));
        }

        if (snapshot.IsEstimate || snapshot.FetchedAt is not { } fetchedAt || fetchedAt > now)
        {
            return [];
        }

        var events = new List<UsageNotificationEvent>(2);
        EvaluateWindow(snapshot.PrimaryWindow, UsageNotificationWindow.Primary);
        EvaluateWindow(snapshot.SecondaryWindow, UsageNotificationWindow.Secondary);
        return events;

        void EvaluateWindow(UsageWindow? window, UsageNotificationWindow windowKind)
        {
            if (window is null || !double.IsFinite(window.UsedPercent) || window.UsedPercent is < 0 or > 100)
            {
                return;
            }

            var key = new WindowKey(snapshot.ProviderId, snapshot.Source ?? string.Empty, segmentId, windowKind);
            var remainingPercent = 100 - window.UsedPercent;
            if (!_windows.TryGetValue(key, out var state))
            {
                _windows[key] = new WindowState(window.ResetAt, window.LimitWindowSeconds, window.UsedPercent, false);
                return;
            }

            if (state.LimitWindowSeconds != window.LimitWindowSeconds)
            {
                _windows[key] = new WindowState(window.ResetAt, window.LimitWindowSeconds, window.UsedPercent, false);
                return;
            }

            var notified = state.ThresholdNotified;
            if (100 - state.UsedPercent > thresholdPercent && remainingPercent <= thresholdPercent && !notified)
            {
                events.Add(new UsageNotificationEvent(
                    snapshot.ProviderId,
                    snapshot.ProviderDisplayName,
                    windowKind,
                    UsageNotificationEventKind.ThresholdReached,
                    remainingPercent));
                notified = true;
            }
            else if (remainingPercent > thresholdPercent)
            {
                notified = false;
            }

            _windows[key] = new WindowState(window.ResetAt, window.LimitWindowSeconds, window.UsedPercent, notified);
        }
    }

    public void ResetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        foreach (var key in _windows.Keys.Where(key => key.ProviderId == providerId).ToArray())
        {
            _windows.Remove(key);
        }
    }

    private readonly record struct WindowKey(
        string ProviderId,
        string Source,
        string SegmentId,
        UsageNotificationWindow Window);

    private sealed record WindowState(DateTimeOffset ResetAt, int LimitWindowSeconds, double UsedPercent, bool ThresholdNotified);
}
