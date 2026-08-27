using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexUsageClient : ICodexUsageClient, IDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _logger;
    private readonly Func<string?>? _accountIdProvider;
    private readonly CodexUsageClientOptions _options;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    public CodexUsageClient(
        HttpClient? httpClient = null,
        Action<string>? logger = null,
        Func<string?>? accountIdProvider = null,
        CodexUsageClientOptions? options = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
        _accountIdProvider = accountIdProvider;
        _options = options ?? new CodexUsageClientOptions();
        _ownsHttpClient = httpClient is null;
    }

    public CodexUsageClient(
        HttpMessageHandler handler,
        Action<string>? logger = null,
        Func<string?>? accountIdProvider = null,
        CodexUsageClientOptions? options = null)
        : this(new HttpClient(handler, disposeHandler: false), logger, accountIdProvider, options)
    {
    }

    public async Task<CodexUsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("An access token is required.", nameof(accessToken));
        }

        ThrowIfDisposed();
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            using var request = CreateRequest(accessToken);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(_options.RequestTimeout);

            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (IsTransient(response.StatusCode) && attempt < _options.MaxAttempts)
                    {
                        await DelayBeforeRetryAsync(response, attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new HttpRequestException($"Codex usage request failed with HTTP {(int)response.StatusCode}.");
                }

                try
                {
                    using var document = JsonDocument.Parse(body);
                    return ParseSnapshot(document.RootElement);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
                {
                    _logger?.Invoke(string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"[TokensLimits] Usage response schema rejected (HTTP {(int)response.StatusCode}, {body.Length} bytes)."));
                    throw new InvalidDataException("Codex usage response has an unsupported format.", ex);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestCts.IsCancellationRequested)
            {
                if (attempt == _options.MaxAttempts)
                {
                    throw new TimeoutException("Codex usage request timed out.");
                }

                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Codex usage request did not complete.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static CodexUsageSnapshot ParseSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex usage response root is not an object.");
        }

        var rateLimit = GetObject(root, "rate_limit") ?? root;
        var primary = ParseWindow(rateLimit, "primary_window");
        var secondary = ParseWindow(rateLimit, "secondary_window");
        if (primary is null && secondary is null)
        {
            throw new InvalidDataException("Codex usage response contains no rate-limit windows.");
        }

        var now = DateTimeOffset.UtcNow;
        var hasPrimaryWindow = primary is not null;
        var hasSecondaryWindow = secondary is not null;
        primary ??= new CodexUsageWindow(0, now, 0);
        secondary ??= new CodexUsageWindow(0, now, 0);

        var snapshot = new CodexUsageSnapshot(
            primary.UsedPercent,
            primary.ResetAt,
            secondary.UsedPercent,
            secondary.ResetAt,
            GetString(root, "plan") ?? GetString(root, "plan_type"),
            false)
        {
            AdditionalRateLimits = ParseAdditionalRateLimits(root),
            HasPrimaryWindow = hasPrimaryWindow,
            HasSecondaryWindow = hasSecondaryWindow,
        };
        return snapshot;
    }

    private static List<CodexAdditionalRateLimit> ParseAdditionalRateLimits(JsonElement root)
    {
        if (!root.TryGetProperty("additional_rate_limits", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<CodexAdditionalRateLimit>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = GetString(entry, "limit_name") ?? GetString(entry, "metered_feature");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var rateLimit = GetObject(entry, "rate_limit") ?? entry;
            var primaryWindow = ParseWindow(rateLimit, "primary_window");
            var secondaryWindow = ParseWindow(rateLimit, "secondary_window");
            if (primaryWindow is null && secondaryWindow is null)
            {
                continue;
            }

            result.Add(new CodexAdditionalRateLimit(
                name,
                primaryWindow,
                secondaryWindow));
        }

        return result;
    }

    private static CodexUsageWindow? ParseWindow(JsonElement parent, string propertyName)
    {
        var window = GetObject(parent, propertyName);
        if (window is null)
        {
            return null;
        }

        var usedPercent = GetDouble(window.Value, "used_percent");
        var resetAt = GetUnixTime(window.Value, "reset_at");
        var limitSeconds = GetInt(window.Value, "limit_window_seconds");
        if (usedPercent is null || resetAt is null || limitSeconds is null)
        {
            return null;
        }

        if (!double.IsFinite(usedPercent.Value)
            || usedPercent.Value is < 0 or > 100
            || limitSeconds.Value <= 0)
        {
            return null;
        }

        return new CodexUsageWindow(usedPercent.Value, resetAt.Value, limitSeconds.Value);
    }

    private static JsonElement? GetObject(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? GetString(JsonElement parent, string propertyName)
    {
        return parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(
            value.GetString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static int? GetInt(JsonElement parent, string propertyName)
    {
        var value = GetDouble(parent, propertyName);
        return value is null
            || !double.IsFinite(value.Value)
            || value.Value < 1
            || value.Value > int.MaxValue
            || value.Value != Math.Truncate(value.Value)
            ? null
            : (int)value.Value;
    }

    private static DateTimeOffset? GetUnixTime(JsonElement parent, string propertyName)
    {
        var value = GetDouble(parent, propertyName);
        if (value is null || value <= 0)
        {
            return null;
        }

        try
        {
            return value > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value)
                : DateTimeOffset.FromUnixTimeSeconds((long)value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private HttpRequestMessage CreateRequest(string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _options.UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        var accountId = _accountIdProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        return request;
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        var retryAfter = response?.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date
                ? date - DateTimeOffset.UtcNow
                : TimeSpan.FromMilliseconds(DefaultRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 10_000));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
