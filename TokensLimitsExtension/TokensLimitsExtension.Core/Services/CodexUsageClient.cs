using System.Net.Http.Headers;
using System.Text.Json;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexUsageClient : ICodexUsageClient
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/codex/usage");
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _logger;

    public CodexUsageClient(HttpClient? httpClient = null, Action<string>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger;
    }

    public CodexUsageClient(HttpMessageHandler handler, Action<string>? logger = null)
        : this(new HttpClient(handler, disposeHandler: false), logger)
    {
    }

    public async Task<CodexUsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("An access token is required.", nameof(accessToken));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("TokensLimitsExtension/1.0");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Codex usage request failed with HTTP {(int)response.StatusCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return ParseSnapshot(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
        {
            _logger?.Invoke($"[TokensLimits] Raw usage JSON: {body}");
            throw new InvalidDataException("Codex usage response has an unsupported format.", ex);
        }
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
            result.Add(new CodexAdditionalRateLimit(
                name,
                ParseWindow(rateLimit, "primary_window"),
                ParseWindow(rateLimit, "secondary_window")));
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
        return value is null ? null : (int)value.Value;
    }

    private static DateTimeOffset? GetUnixTime(JsonElement parent, string propertyName)
    {
        var value = GetDouble(parent, propertyName);
        if (value is null || value <= 0)
        {
            return null;
        }

        return value > 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value)
            : DateTimeOffset.FromUnixTimeSeconds((long)value.Value);
    }
}
