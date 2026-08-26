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
            Assert.Equal(10, snapshot.PrimaryUsedPercent);
            Assert.Equal(1, snapshot.SecondaryUsedPercent);
            Assert.Equal(now.AddHours(5), snapshot.PrimaryResetAt);
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

            Assert.Equal(10, snapshot.PrimaryUsedPercent);
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

            Assert.Equal(first.PrimaryUsedPercent, second.PrimaryUsedPercent);
            Assert.Equal(10, first.PrimaryUsedPercent);
            Assert.Equal(20, third.PrimaryUsedPercent);
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
