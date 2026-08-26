using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class CodexLocalSessionFallbackTests
{
    [Fact]
    public async Task CountsRecentTokenDeltasAndMarksSnapshotAsEstimate()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions", "project-alpha");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        await File.WriteAllLinesAsync(file, [
            $"{{\"timestamp\":\"{now.AddHours(-1):O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":1000}}}}}}}}",
            $"{{\"timestamp\":\"{now.AddDays(-8):O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":9000}}}}}}}}",
        ]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, 10_000, 100_000, new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.IsEstimate);
            Assert.Equal(10, snapshot.PrimaryUsedPercent);
            Assert.Equal(1, snapshot.SecondaryUsedPercent);
            Assert.Equal(now.AddHours(5), snapshot.PrimaryResetAt);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
