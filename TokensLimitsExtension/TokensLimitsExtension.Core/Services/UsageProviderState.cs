using System.Net;
using System.Text.Json;
using System.Xml;
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

/// <summary>Allows the scheduler to stop a shared refresh after provider removal.</summary>
internal interface IRefreshCancellationSource
{
    void CancelRefreshForDeactivation();
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

        if (exception is HttpRequestException)
        {
            return UsageProviderErrorKind.Network;
        }

        if (exception is UsageProviderRequestException providerException)
        {
            var statusKind = providerException.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => UsageProviderErrorKind.Authentication,
                HttpStatusCode.TooManyRequests => UsageProviderErrorKind.RateLimited,
                _ => (UsageProviderErrorKind?)null,
            };
            if (statusKind is { } resolvedStatusKind)
            {
                return resolvedStatusKind;
            }

            return providerException.FailureKind switch
            {
                UsageProviderFailureKind.Authentication => UsageProviderErrorKind.Authentication,
                UsageProviderFailureKind.RateLimited => UsageProviderErrorKind.RateLimited,
                UsageProviderFailureKind.Timeout => UsageProviderErrorKind.Timeout,
                UsageProviderFailureKind.Network => UsageProviderErrorKind.Network,
                UsageProviderFailureKind.UnsupportedResponse => UsageProviderErrorKind.UnsupportedResponse,
                UsageProviderFailureKind.Unknown when providerException.InnerException is not null
                    => Classify(providerException.InnerException),
                _ => UsageProviderErrorKind.UnsupportedResponse,
            };
        }

        if (exception is JsonException or InvalidDataException or FormatException or XmlException)
        {
            return UsageProviderErrorKind.UnsupportedResponse;
        }

        return UsageProviderErrorKind.Unknown;
    }

    public static TimeSpan? GetRetryAfter(Exception exception)
        => exception is UsageProviderRequestException { RetryAfter: { } retryAfter }
            ? retryAfter
            : null;

}
