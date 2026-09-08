using TokensLimitsExtension.Core.Services;
using System.Text.Json;

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
            Assert.False(snapshot.HasPrimaryWindow);
            Assert.False(snapshot.HasSecondaryWindow);
            Assert.Equal(1000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(1000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task SkipsLockedSessionFileAndReadsOtherSessions()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions", "project-alpha");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var lockedFile = Path.Combine(sessions, "active.jsonl");
        var readableFile = Path.Combine(sessions, "completed.jsonl");
        await File.WriteAllTextAsync(lockedFile, "not readable while locked");
        await File.WriteAllTextAsync(readableFile,
            $"{{\"timestamp\":\"{now.AddHours(-1):O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":1000}}}}}}}}\n");
        using var lockHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var provider = new CodexLocalSessionFallback(home, 10_000, 100_000, new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(1000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        }
        finally
        {
            lockHandle.Dispose();
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task ReusesUnchangedFileAndInvalidatesItWhenItGrows()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        var firstLine = CreateTokenCountLine(now.AddHours(-1));
        await File.WriteAllTextAsync(file, firstLine + Environment.NewLine);

        try
        {
            var provider = new CodexLocalSessionFallback(home, 10_000, 100_000, new FixedTimeProvider(now));

            var first = await provider.GetSnapshotAsync(CancellationToken.None);
            var second = await provider.GetSnapshotAsync(CancellationToken.None);
            await File.AppendAllTextAsync(file,
                CreateTokenCountLine(now.AddMinutes(-30)) + Environment.NewLine);
            var third = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(first.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue, second.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(1000, first.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(2000, third.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task ThrowsWhenNoReadableTokenUsageExists()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(DateTimeOffset.UtcNow));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => provider.GetSnapshotAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task SaturatesMalformedTokenCountsInsteadOfOverflowing()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"), [
            $"{{\"timestamp\":\"{now.AddHours(-1):O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":9223372036854775807}}}}}}}}",
            $"{{\"timestamp\":\"{now.AddMinutes(-30):O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":9223372036854775807}}}}}}}}",
        ]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, 10_000, 100_000, new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(long.MaxValue, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(long.MaxValue, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task PrefersCumulativeCountersAndAccountsForCounterReset()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"), [
            $"{{\"timestamp\":\"{now.AddHours(-2):O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":100}},\"last_token_usage\":{{\"total_tokens\":999}}}}}}}}",
            $"{{\"timestamp\":\"{now.AddHours(-1):O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":150}}}}}}}}",
            $"{{\"timestamp\":\"{now.AddMinutes(-30):O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":20}}}}}}}}",
        ]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(170, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
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

    private static string CreateTokenCountLine(DateTimeOffset timestamp)
        => JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new { total_tokens = 1000 },
                },
            },
        });
}
