namespace TokensLimitsExtension.Core.Services;

/// <summary>
/// The transport contract used by <see cref="CodexUsageClient"/>.
/// Keeping the backend details here makes an upstream endpoint change explicit
/// and keeps the request implementation independently testable.
/// </summary>
public sealed class CodexUsageClientOptions
{
    public CodexUsageClientOptions(
        Uri? usageEndpoint = null,
        string userAgent = "codex-cli",
        TimeSpan? requestTimeout = null,
        int maxAttempts = 3)
    {
        UsageEndpoint = usageEndpoint ?? new Uri("https://chatgpt.com/backend-api/wham/usage");
        UserAgent = string.IsNullOrWhiteSpace(userAgent)
            ? throw new ArgumentException("A User-Agent value is required.", nameof(userAgent))
            : userAgent;
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        MaxAttempts = maxAttempts;

        if (!UsageEndpoint.IsAbsoluteUri || !string.Equals(UsageEndpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Codex usage endpoint must be an absolute HTTPS URI.", nameof(usageEndpoint));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The request timeout must be positive.");
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one request attempt is required.");
        }
    }

    public Uri UsageEndpoint { get; }

    public string UserAgent { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaxAttempts { get; }
}
