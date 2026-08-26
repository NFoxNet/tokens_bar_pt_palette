using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class CodexUsageServiceTests
{
    [Fact]
    public async Task UsesLocalFallbackWhenPrimaryPathFails()
    {
        var expected = new CodexUsageSnapshot(10, DateTimeOffset.UtcNow, 20, DateTimeOffset.UtcNow, null, true);
        var logs = new List<string>();
        var service = new CodexUsageService(
            new StubAuth(() => throw new InvalidOperationException("auth unavailable")),
            new StubClient(_ => throw new InvalidOperationException("not used")),
            new StubFallback(() => Task.FromResult(expected)),
            logs.Add);

        var actual = await service.GetSnapshotAsync();

        Assert.Equal(expected, actual);
        Assert.True(actual.IsEstimate);
        Assert.Contains(logs, message => message.Contains("Fallback triggered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogsSuccessfulOfficialSnapshot()
    {
        var expected = new CodexUsageSnapshot(12, DateTimeOffset.UtcNow, 34, DateTimeOffset.UtcNow, "pro", false);
        var logs = new List<string>();
        var service = new CodexUsageService(
            new StubAuth(() => Task.FromResult("token")),
            new StubClient(_ => Task.FromResult(expected)),
            new StubFallback(() => throw new InvalidOperationException("not used")),
            logs.Add);

        var actual = await service.GetSnapshotAsync();

        Assert.Equal(expected, actual);
        Assert.Contains(logs, message => message.Contains("Snapshot fetched", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, message => message.Contains("token", StringComparison.Ordinal));
    }

    private sealed class StubAuth(Func<Task<string>> callback) : ICodexAuthTokenProvider
    {
        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken) => callback();
    }

    private sealed class StubClient(Func<string, Task<CodexUsageSnapshot>> callback) : ICodexUsageClient
    {
        public Task<CodexUsageSnapshot> FetchUsageAsync(string accessToken, CancellationToken cancellationToken)
            => callback(accessToken);
    }

    private sealed class StubFallback(Func<Task<CodexUsageSnapshot>> callback) : ICodexUsageFallback
    {
        public Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => callback();
    }
}
