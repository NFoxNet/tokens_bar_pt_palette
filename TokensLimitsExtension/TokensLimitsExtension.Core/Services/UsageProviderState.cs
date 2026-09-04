using System.Net;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;

namespace TokensLimitsExtension.Core.Services;

/// <summary>Safe, UI-facing status of a provider refresh.</summary>
public sealed record UsageProviderState(
    UsageSnapshot? Snapshot,
    DateTimeOffset? LastSuccessfulRefreshAt,
    DateTimeOffset? LastAttemptAt,
    bool IsRefreshing,
    UsageProviderErrorKind ErrorKind = UsageProviderErrorKind.None,
    TimeSpan? RetryAfter = null)
{
    public bool IsStale => Snapshot is not null && ErrorKind != UsageProviderErrorKind.None;
}

public enum UsageProviderErrorKind
{
    None,
    MissingConfiguration,
    Authentication,
    RateLimited,
    Timeout,
    Network,
    UnsupportedResponse,
    Unknown,
}

public interface IUsageProviderStateSource : IRefreshableUsageProvider
{
    UsageProviderState State { get; }

    event EventHandler? StateChanged;

    Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default);
}

internal static class UsageProviderErrorClassifier
{
    public static UsageProviderErrorKind Classify(Exception exception)
    {
        if (exception is UsageProviderConfigurationException)
        {
            return UsageProviderErrorKind.MissingConfiguration;
        }

        if (exception is TimeoutException or TaskCanceledException)
        {
            return UsageProviderErrorKind.Timeout;
        }

        if (exception is HttpRequestException requestException)
        {
            return ClassifyMessage(requestException.Message, UsageProviderErrorKind.Network);
        }

        if (exception is UsageProviderRequestException providerException)
        {
            return ClassifyMessage(providerException.Message, UsageProviderErrorKind.UnsupportedResponse);
        }

        return ClassifyMessage(exception.Message, UsageProviderErrorKind.Unknown);
    }

    public static TimeSpan? GetRetryAfter(Exception exception)
        => exception is UsageProviderRequestException { RetryAfter: { } retryAfter }
            ? retryAfter
            : null;

    private static UsageProviderErrorKind ClassifyMessage(string? message, UsageProviderErrorKind fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        if (message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal)
            || message.Contains("unauthor", StringComparison.OrdinalIgnoreCase)
            || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProviderErrorKind.Authentication;
        }

        if (message.Contains("429", StringComparison.Ordinal)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return UsageProviderErrorKind.RateLimited;
        }

        return fallback;
    }
}
