using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class CodexFileAuthTokenProviderTests
{
    [Fact]
    public async Task ReadsUnexpiredTokenFromNestedTokensObject()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                tokens = new
                {
                    access_token = "access-token",
                    refresh_token = "refresh-token",
                    expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                },
            }));

            var provider = new CodexFileAuthTokenProvider(authPath);

            Assert.Equal("access-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task RefreshesExpiredTokenUsingOAuthEndpoint()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-token", refresh_token = "rotated-refresh-token" }),
        });
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                access_token = "expired-token",
                refresh_token = "refresh-token",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            }));

            var provider = new CodexFileAuthTokenProvider(authPath, handler);

            Assert.Equal("refreshed-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
            Assert.NotNull(handler.Request);
            Assert.Equal(HttpMethod.Post, handler.Request!.Method);
            Assert.Equal("https://auth.openai.com/oauth/token", handler.Request.RequestUri!.ToString());
            Assert.Contains("refresh_token", handler.RequestBody, StringComparison.Ordinal);
            Assert.Contains("grant_type", handler.RequestBody, StringComparison.Ordinal);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));
            Assert.Equal("refreshed-token", persisted.RootElement.GetProperty("access_token").GetString());
            Assert.Equal("rotated-refresh-token", persisted.RootElement.GetProperty("refresh_token").GetString());
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task RejectsExpiredTokenWithoutRefreshToken()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(authPath, "{\"access_token\":\"expired\",\"expires_at\":0}");
            var provider = new CodexFileAuthTokenProvider(authPath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.GetValidAccessTokenAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task ReadsAccountIdAndRefreshesJwtExpiredToken()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-token" }),
        });
        var expiredPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{{\"exp\":{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()}}}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                tokens = new
                {
                    access_token = $"header.{expiredPayload}.signature",
                    refresh_token = "refresh-token",
                    account_id = "account-id",
                },
            }));

            var provider = new CodexFileAuthTokenProvider(authPath, handler);

            Assert.Equal("refreshed-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
            Assert.Equal("account-id", provider.AccountId);
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task CachesRefreshedTokenBetweenCalls()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "refreshed-token" }),
        });
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                access_token = "expired-token",
                refresh_token = "refresh-token",
                expires_at = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            }));

            var provider = new CodexFileAuthTokenProvider(authPath, handler);

            Assert.Equal("refreshed-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
            Assert.Equal("refreshed-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task ReloadsAnUnexpiredTokenWhenCodexUpdatesAuthFile()
    {
        var authPath = Path.Combine(Path.GetTempPath(), $"codex-auth-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                access_token = "first-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            }));
            var provider = new CodexFileAuthTokenProvider(authPath);
            Assert.Equal("first-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));

            await File.WriteAllTextAsync(authPath, JsonSerializer.Serialize(new
            {
                access_token = "updated-token",
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            }));
            File.SetLastWriteTimeUtc(authPath, DateTime.UtcNow.AddSeconds(2));

            Assert.Equal("updated-token", await provider.GetValidAccessTokenAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(authPath);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            RequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
