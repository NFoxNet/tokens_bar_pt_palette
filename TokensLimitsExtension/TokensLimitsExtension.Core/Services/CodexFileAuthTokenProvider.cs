using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedExpiresAt;
    private string? _cachedRefreshToken;
    private string? _cachedAccountId;
    private DateTime _cachedAuthFileLastWriteTimeUtc;
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
        _ownsHttpClient = true;
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        await _tokenGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var now = _timeProvider.GetUtcNow();
            if (HasCurrentCachedAccessToken(now))
            {
                return _cachedAccessToken!;
            }

            var token = await ReadAndRefreshIfNeededAsync(now, linkedCts.Token).ConfigureAwait(false);
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

        _lifetimeCts.Cancel();
        // An active or queued token request still releases the semaphore in its
        // finally block. Let the managed gate and CTS be reclaimed with this provider.
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

        var authFileLastWriteTimeUtc = GetAuthFileLastWriteTimeUtc();
        await using var stream = new FileStream(
            _authFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tokenObject = TryGetObject(root, "tokens") ?? root;

        var accessToken = GetString(tokenObject, "access_token") ?? GetString(tokenObject, "accessToken");
        var refreshToken = GetString(tokenObject, "refresh_token")
            ?? GetString(tokenObject, "refreshToken")
            ?? _cachedRefreshToken;
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
            CacheToken(accessToken, expiresAt ?? now.AddMinutes(5), authFileLastWriteTimeUtc);
            return accessToken;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Codex access token has expired and no refresh_token is available.");
        }

        // Release the source file before atomically replacing it with a rotated
        // credential set below. The values needed for the refresh are already copied.
        document.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
        var refreshed = await RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
        {
            _cachedRefreshToken = refreshed.RefreshToken;
        }

        var refreshedExpiresAt = refreshed.ExpiresAt ?? GetJwtExpiry(refreshed.AccessToken) ?? now.AddMinutes(5);
        var persistedLastWriteTimeUtc = await PersistRefreshedTokensIfUnchangedAsync(
            authFileLastWriteTimeUtc,
            refreshed.AccessToken,
            refreshed.RefreshToken ?? refreshToken,
            refreshedExpiresAt,
            cancellationToken).ConfigureAwait(false);
        CacheToken(refreshed.AccessToken, refreshedExpiresAt, persistedLastWriteTimeUtc ?? authFileLastWriteTimeUtc);
        return refreshed.AccessToken;
    }

    private async Task<RefreshedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
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

        var expiresAt = GetDateTimeOffset(document.RootElement, "expires_at")
            ?? GetDateTimeOffset(document.RootElement, "expiresAt");
        if (expiresAt is null && TryGetInt64(document.RootElement, "expires_in", out var expiresInSeconds))
        {
            expiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(0, expiresInSeconds));
        }

        return new RefreshedToken(
            refreshedToken,
            GetString(document.RootElement, "refresh_token") ?? GetString(document.RootElement, "refreshToken"),
            expiresAt);
        }
    }

    private bool HasCurrentCachedAccessToken(DateTimeOffset now)
    {
        return !string.IsNullOrWhiteSpace(_cachedAccessToken)
            && _cachedExpiresAt > now.AddMinutes(1)
            && _cachedAuthFileLastWriteTimeUtc == GetAuthFileLastWriteTimeUtc();
    }

    private DateTime GetAuthFileLastWriteTimeUtc()
    {
        try
        {
            return File.GetLastWriteTimeUtc(_authFilePath);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private async Task<DateTime?> PersistRefreshedTokensIfUnchangedAsync(
        DateTime expectedLastWriteTimeUtc,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        // Do not overwrite a token file that Codex CLI refreshed while this OAuth
        // request was in flight. The newer CLI-authored state is authoritative.
        if (GetAuthFileLastWriteTimeUtc() != expectedLastWriteTimeUtc)
        {
            return null;
        }

        var temporaryPath = $"{_authFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var raw = await File.ReadAllTextAsync(_authFilePath, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(raw) as JsonObject
                ?? throw new InvalidDataException("Codex auth.json root is not an object.");
            var tokenObject = root["tokens"] as JsonObject ?? root;
            tokenObject["access_token"] = accessToken;
            tokenObject["refresh_token"] = refreshToken;
            tokenObject["expires_at"] = expiresAt.ToUnixTimeSeconds();

            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _authFilePath, overwrite: true);
            return GetAuthFileLastWriteTimeUtc();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // The refreshed token is still usable in this process. Do not expose
            // authentication values in diagnostics if persisting them fails.
            System.Diagnostics.Debug.WriteLine($"[TokensLimits] WARNING: unable to persist refreshed Codex auth state: {ex.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a non-secret temporary path.
            }
        }
    }

    private void CacheToken(string accessToken, DateTimeOffset expiresAt, DateTime authFileLastWriteTimeUtc)
    {
        _cachedAccessToken = accessToken;
        _cachedExpiresAt = expiresAt;
        _cachedAuthFileLastWriteTimeUtc = authFileLastWriteTimeUtc;
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

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
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

    private sealed record RefreshedToken(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt);
}
