using System.Net;

namespace TokensLimitsExtension.Core.Providers;

public sealed class UsageProviderConfigurationException(string message) : InvalidOperationException(message);

public sealed class UsageProviderRequestException(
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null,
    HttpStatusCode? statusCode = null,
    UsageProviderFailureKind failureKind = UsageProviderFailureKind.Unknown)
    : InvalidOperationException(message, innerException)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public HttpStatusCode? StatusCode { get; } = statusCode;

    public UsageProviderFailureKind FailureKind { get; } = failureKind;
}
