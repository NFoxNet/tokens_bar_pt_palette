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
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(5);
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

        if (snapshot.FetchedAt is not { } fetchedAt
            || fetchedAt > now
            || now - fetchedAt > MaximumSnapshotAge)
        {
            return [];
        }

        var source = snapshot.Source ?? string.Empty;
        var accountScope = snapshot.AccountScope ?? string.Empty;
        foreach (var oldKey in _windows.Keys.Where(existing =>
                     existing.ProviderId == snapshot.ProviderId
                     && existing.SegmentId == segmentId
                     && (snapshot.IsEstimate
                         || existing.Source != source
                         || existing.AccountScope != accountScope)).ToArray())
        {
            _windows.Remove(oldKey);
        }

        if (snapshot.IsEstimate)
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

            var key = new WindowKey(
                snapshot.ProviderId,
                source,
                accountScope,
                segmentId,
                windowKind);
            var remainingPercent = 100 - window.UsedPercent;
            if (!_windows.TryGetValue(key, out var state))
            {
                _windows[key] = WindowState.From(window, fetchedAt, false);
                return;
            }

            if (state.LimitWindowSeconds != window.LimitWindowSeconds || state.IsFixedWindow != window.IsFixedWindow)
            {
                _windows[key] = WindowState.From(window, fetchedAt, false);
                return;
            }

            if (fetchedAt <= state.FetchedAt)
            {
                return;
            }

            if (IsConfirmedReset(state, window, fetchedAt, now))
            {
                events.Add(new UsageNotificationEvent(
                    snapshot.ProviderId,
                    snapshot.ProviderDisplayName,
                    windowKind,
                    UsageNotificationEventKind.WindowReset,
                    remainingPercent));
                _windows[key] = WindowState.From(window, fetchedAt, false);
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
            else if (!state.IsFixedWindow && remainingPercent > thresholdPercent)
            {
                notified = false;
            }

            _windows[key] = WindowState.From(window, fetchedAt, notified);
        }
    }

    private static bool IsConfirmedReset(
        WindowState previous,
        UsageWindow current,
        DateTimeOffset currentFetchedAt,
        DateTimeOffset now)
        => previous.IsFixedWindow
            && current.IsFixedWindow
            && previous.StartsAt is { } previousStartsAt
            && previous.ResetAt is { } previousResetAt
            && current.StartsAt is { } currentStartsAt
            && current.ResetAt is { } currentResetAt
            && current.LimitWindowSeconds > 0
            && previousStartsAt < previousResetAt
            && currentStartsAt < currentResetAt
            && previous.FetchedAt < previousResetAt
            && previousResetAt <= now
            && currentStartsAt > previousStartsAt
            && currentStartsAt >= previousResetAt
            && currentStartsAt <= currentFetchedAt
            && currentFetchedAt < currentResetAt;

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
        string AccountScope,
        string SegmentId,
        UsageNotificationWindow Window);

    private sealed record WindowState(
        DateTimeOffset? StartsAt,
        DateTimeOffset? ResetAt,
        int LimitWindowSeconds,
        double UsedPercent,
        DateTimeOffset FetchedAt,
        bool IsFixedWindow,
        bool ThresholdNotified)
    {
        public static WindowState From(UsageWindow window, DateTimeOffset fetchedAt, bool thresholdNotified)
            => new(
                window.StartsAt,
                window.ResetAt,
                window.LimitWindowSeconds,
                window.UsedPercent,
                fetchedAt,
                window.IsFixedWindow,
                thresholdNotified);
    }
}
