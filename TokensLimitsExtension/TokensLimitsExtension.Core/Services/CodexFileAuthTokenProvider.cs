using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace TokensLimitsExtension.Core.Services;

public sealed class CodexFileAuthTokenProvider : ICodexAuthTokenProvider, ICodexAccountIdentityProvider, IDisposable
{
    private const string RefreshEndpoint = "https://auth.openai.com/oauth/token";
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly string _authFilePath;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedExpiresAt;
    private string? _cachedRefreshToken;
    private string? _cachedAccountId;
    private int _disposed;

    public string? AccountId => Volatile.Read(ref _cachedAccountId);

    public CodexFileAuthTokenProvider(
        string authFilePath,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        _authFilePath = authFilePath ?? throw new ArgumentNullException(nameof(authFilePath));
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsHttpClient = httpClient is null;
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
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken)
                && _cachedExpiresAt > now.AddMinutes(1))
            {
                return _cachedAccessToken;
            }

            var token = await ReadAndRefreshIfNeededAsync(now, cancellationToken).ConfigureAwait(false);
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _tokenGate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<string> ReadAndRefreshIfNeededAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
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
        var accountId = GetString(tokenObject, "account_id")
            ?? GetString(tokenObject, "accountId")
            ?? GetString(root, "account_id")
            ?? GetString(root, "accountId");
        var expiresAt = GetDateTimeOffset(tokenObject, "expires_at") ?? GetDateTimeOffset(tokenObject, "expiresAt");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidDataException("Codex auth.json does not contain access_token.");
        }

        Volatile.Write(ref _cachedAccountId, accountId);
        _cachedRefreshToken = refreshToken;
        expiresAt ??= GetJwtExpiry(accessToken);
        if (expiresAt is null || expiresAt > now.AddMinutes(1))
        {
            CacheToken(accessToken, expiresAt ?? now.AddMinutes(5));
            return accessToken;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Codex access token has expired and no refresh_token is available.");
        }

        var refreshedToken = await RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        CacheToken(refreshedToken, GetJwtExpiry(refreshedToken) ?? now.AddMinutes(5));
        return refreshedToken;
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
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestCts.IsCancellationRequested)
        {
            throw new TimeoutException("Codex token refresh request timed out.");
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
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
    }

    private void CacheToken(string accessToken, DateTimeOffset expiresAt)
    {
        _cachedAccessToken = accessToken;
        _cachedExpiresAt = expiresAt;
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
            return TryFromUnixTime(unixValue);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stringUnixValue))
            {
                return TryFromUnixTime(stringUnixValue);
            }

            if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? GetJwtExpiry(string accessToken)
    {
        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return GetDateTimeOffset(document.RootElement, "exp");
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryFromUnixTime(long value)
    {
        try
        {
            return value > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
