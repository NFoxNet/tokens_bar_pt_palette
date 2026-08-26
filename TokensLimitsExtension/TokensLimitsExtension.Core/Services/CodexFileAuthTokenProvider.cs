using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexFileAuthTokenProvider : ICodexAuthTokenProvider
{
    private const string RefreshEndpoint = "https://auth.openai.com/oauth/token";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly string _authFilePath;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public CodexFileAuthTokenProvider(
        string authFilePath,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _authFilePath = authFilePath ?? throw new ArgumentNullException(nameof(authFilePath));
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CodexFileAuthTokenProvider(
        string authFilePath,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null)
        : this(authFilePath, new HttpClient(handler, disposeHandler: false), timeProvider)
    {
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_authFilePath))
        {
            throw new FileNotFoundException($"Codex auth.json not found: {_authFilePath}", _authFilePath);
        }

        await using var stream = File.OpenRead(_authFilePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tokenObject = TryGetObject(root, "tokens") ?? root;

        var accessToken = GetString(tokenObject, "access_token") ?? GetString(tokenObject, "accessToken");
        var refreshToken = GetString(tokenObject, "refresh_token") ?? GetString(tokenObject, "refreshToken");
        var expiresAt = GetDateTimeOffset(tokenObject, "expires_at") ?? GetDateTimeOffset(tokenObject, "expiresAt");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("Codex auth.json does not contain access_token.");
        }

        var now = _timeProvider.GetUtcNow();
        if (expiresAt is null || expiresAt > now.AddMinutes(1))
        {
            return accessToken;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Codex access token has expired and no refresh_token is available.");
        }

        return await RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            client_id = ClientId,
            grant_type = "refresh_token",
            refresh_token = refreshToken,
            scope = "openid profile email",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Codex token refresh failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(responseBody);
        var refreshedToken = GetString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(refreshedToken))
        {
            throw new InvalidDataException("Codex token refresh response does not contain access_token.");
        }

        return refreshedToken;
    }

    private static JsonElement? TryGetObject(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixValue))
        {
            return unixValue > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                : DateTimeOffset.FromUnixTimeSeconds(unixValue);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, out var stringUnixValue))
            {
                return stringUnixValue > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(stringUnixValue)
                    : DateTimeOffset.FromUnixTimeSeconds(stringUnixValue);
            }

            if (DateTimeOffset.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
