using System.Net;
using System.Text.Json;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class CodexCompositionTests
{
    [Fact]
    public void ResolvesEmptyOrWhitespaceHomeToDefaultCodexDirectory()
    {
        var profile = Path.Combine(Path.GetTempPath(), "codex-profile");

        Assert.Equal(Path.Combine(profile, ".codex"), Assert.Single(TokensLimitsExtensionCommandsProvider.ParseCodexHomes(null, profile)));
        Assert.Equal(Path.Combine(profile, ".codex"), Assert.Single(TokensLimitsExtensionCommandsProvider.ParseCodexHomes(" ,  , ", profile)));
    }

    [Fact]
    public void TrimsSingleAndMultipleCodexHomesWithoutDroppingEntries()
    {
        var profile = Path.Combine(Path.GetTempPath(), "codex-profile");
        var firstHome = Path.Combine(profile, "first");
        var secondHome = Path.Combine(profile, "second");

        Assert.Equal([firstHome], TokensLimitsExtensionCommandsProvider.ParseCodexHomes($" {firstHome} ", profile));
        Assert.Equal([firstHome, secondHome], TokensLimitsExtensionCommandsProvider.ParseCodexHomes($" {firstHome} , {secondHome} ", profile));
    }

    [Fact]
    public async Task DefaultCompositionReadsAuthFromFirstHomeAndFallbackSessionsFromEveryHome()
    {
        using var directory = new TestDirectory();
        var firstHome = Path.Combine(directory.Path, "first");
        var secondHome = Path.Combine(directory.Path, "second");
        Directory.CreateDirectory(firstHome);
        Directory.CreateDirectory(secondHome);
        await WriteAuthAsync(firstHome, "first-home-token");
        await WriteAuthAsync(secondHome, "wrong-second-home-token");
        await WriteSessionAsync(firstHome, 100);
        await WriteSessionAsync(secondHome, 250);
        using var handler = new UnauthorizedUsageHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        using var service = TokensLimitsExtensionCommandsProvider.CreateDefaultService(
            $" {firstHome} , {secondHome} ",
            httpClient,
            directory.Path);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("Bearer first-home-token", handler.Authorization);
        Assert.Equal(350, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        Assert.True(snapshot.IsEstimate);
    }

    private static async Task WriteAuthAsync(string home, string token)
    {
        await File.WriteAllTextAsync(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new
        {
            tokens = new
            {
                access_token = token,
                expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            },
        }));
    }

    private static async Task WriteSessionAsync(string home, long totalTokens)
    {
        var sessionDirectory = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { last_token_usage = new { total_tokens = totalTokens } },
            },
        });
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "session.jsonl"), line + "\n");
    }

    private sealed class UnauthorizedUsageHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tokens-limits-codex-composition-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
