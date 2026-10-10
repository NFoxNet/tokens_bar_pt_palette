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

    [Fact]
    public async Task AccountSwitchChangesOpaqueScopeWithoutExposingCredentials()
    {
        var auth = new IdentityAuth { AccountId = "fake-account-a", Token = "fake-token-a" };
        var logs = new List<string>();
        using var service = new CodexUsageService(auth,
            new StubClient(_ => Task.FromResult(new CodexUsageSnapshot(12, DateTimeOffset.UtcNow, 34, DateTimeOffset.UtcNow, null, false))),
            new StubFallback(() => throw new InvalidOperationException("not used")), logs.Add);

        var first = await service.GetSnapshotAsync();
        auth.AccountId = "fake-account-b";
        auth.Token = "fake-token-b";
        var second = await service.GetSnapshotAsync();

        var firstScope = Assert.IsType<string>(first.AccountScope);
        var secondScope = Assert.IsType<string>(second.AccountScope);
        Assert.Equal(64, firstScope.Length);
        Assert.NotEqual(firstScope, secondScope);
        var serialized = System.Text.Json.JsonSerializer.Serialize(first);
        Assert.DoesNotContain(firstScope, serialized);
        foreach (var credential in new[] { "fake-account-a", "fake-account-b", "fake-token-a", "fake-token-b" })
        {
            Assert.DoesNotContain(credential, serialized);
            Assert.DoesNotContain(logs, message => message.Contains(credential, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task CapturesAccountBeforeFetchingResponse()
    {
        var auth = new IdentityAuth { AccountId = "fake-account-a" };
        using var service = new CodexUsageService(auth,
            new StubClient(_ =>
            {
                auth.AccountId = "fake-account-b";
                return Task.FromResult(new CodexUsageSnapshot(12, DateTimeOffset.UtcNow, 34, DateTimeOffset.UtcNow, null, false));
            }), new StubFallback(() => throw new InvalidOperationException("not used")));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fake-account-a"))), snapshot.AccountScope);
    }

    [Fact]
    public async Task ConcurrentRequestsKeepTokenAndIdentityTogether()
    {
        var auth = new IdentityAuth { AccountId = "fake-account-a" };
        auth.OnRead = () =>
        {
            auth.AccountId = auth.ReadCount == 1 ? "fake-account-a" : "fake-account-b";
            auth.Token = auth.ReadCount == 1 ? "fake-token-a" : "fake-token-b";
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new CodexUsageService(auth,
            new StubClient(async token =>
            {
                Assert.Equal(token.Replace("token", "account", StringComparison.Ordinal), auth.AccountId);
                entered.TrySetResult();
                await release.Task;
                return new CodexUsageSnapshot(12, DateTimeOffset.UtcNow, 34, DateTimeOffset.UtcNow, null, false);
            }), new StubFallback(() => throw new InvalidOperationException("not used")));
        var first = service.GetSnapshotAsync();
        await entered.Task;
        var second = service.GetSnapshotAsync();
        var readsWhileFirstPending = auth.ReadCount;
        release.SetResult();
        var snapshots = await Task.WhenAll(first, second);

        Assert.Equal(1, readsWhileFirstPending);
        Assert.Equal(2, auth.ReadCount);
        Assert.NotEqual(snapshots[0].AccountScope, snapshots[1].AccountScope);
    }

    [Fact]
    public async Task FailedAuthenticationDoesNotAssociateFallbackWithCachedAccount()
    {
        var auth = new IdentityAuth { AccountId = "fake-account-a", Fail = true };
        using var service = new CodexUsageService(auth,
            new StubClient(_ => throw new InvalidOperationException("not used")),
            new StubFallback(() => Task.FromResult(new CodexUsageSnapshot(0, DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, null, true))));
        var snapshot = await service.GetSnapshotAsync();

        Assert.Null(snapshot.AccountScope);
    }

    private sealed class IdentityAuth : ICodexAuthTokenProvider, ICodexAccountIdentityProvider
    {
        public string? AccountId { get; set; }
        public string Token { get; set; } = "fake-token";
        public bool Fail { get; set; }
        public int ReadCount { get; private set; }
        public Action? OnRead { get; set; }

        public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            OnRead?.Invoke();
            return Fail ? Task.FromException<string>(new InvalidOperationException("auth unavailable")) : Task.FromResult(Token);
        }
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
