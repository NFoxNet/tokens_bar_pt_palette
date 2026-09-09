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

    [Fact]
    public async Task CumulativeCounterUsesTheWindowBaselineInsteadOfHistoricalTotal()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"), [
            CreateCumulativeTokenCountLine(now.AddDays(-30), 100),
            CreateCumulativeTokenCountLine(now.AddHours(-1), 150),
        ]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(50, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(50, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task IncludesWindowBoundaryAndIgnoresFutureOrExpiredEvents()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"), [
            CreateTokenCountLine(now - TimeSpan.FromHours(5), 1000),
            CreateTokenCountLine(now.AddHours(-1), 2000),
            CreateTokenCountLine(now.AddDays(-8), 4000),
            CreateTokenCountLine(now.AddMinutes(1), 8000),
        ]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(3000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(3000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task ReReadsAFormerPartialLineWhenItIsCompleted()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        var line = CreateTokenCountLine(now.AddHours(-1));
        await File.WriteAllTextAsync(file, line[..^1]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetSnapshotAsync(CancellationToken.None));
            await File.AppendAllTextAsync(file, "}" + Environment.NewLine);

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(1000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task ResumesAfterTheLastCompleteLineWhenAPartialTailGrows()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        var firstLine = CreateTokenCountLine(now.AddHours(-2), 1000);
        var secondLine = CreateTokenCountLine(now.AddHours(-1), 2000);
        await File.WriteAllTextAsync(file, firstLine + Environment.NewLine + secondLine[..^1]);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            var initial = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(1000, initial.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            await File.AppendAllTextAsync(file, "}" + Environment.NewLine);

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(3000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task PartialTailAppendReadsOnlyTheBoundedTailAfterTheInitialPass()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        var lines = Enumerable.Range(0, 3_000)
            .Select(index => CreateTokenCountLine(now.AddMinutes(-index), 1))
            .ToArray();
        var partialLine = CreateTokenCountLine(now, 1);
        await File.WriteAllTextAsync(file, string.Join(Environment.NewLine, lines) + Environment.NewLine + partialLine[..^1]);

        try
        {
            var reads = new List<long>();
            var provider = new CodexLocalSessionFallback(
                home,
                timeProvider: new FixedTimeProvider(now),
                readBytesObserver: bytes => reads.Add(bytes));

            await provider.GetSnapshotAsync(CancellationToken.None);
            var initialBytes = reads.Sum();
            reads.Clear();
            await File.AppendAllTextAsync(file, "}" + Environment.NewLine);
            await provider.GetSnapshotAsync(CancellationToken.None);
            var appendBytes = reads.Sum();

            Assert.True(initialBytes > 300_000, $"Initial pass read only {initialBytes} bytes.");
            Assert.True(appendBytes < initialBytes, $"Append pass read {appendBytes} bytes after an initial {initialBytes}-byte pass.");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task SkipsAnOversizedJsonlLineWithoutHoldingItsContentsInMemory()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var oversizedLine = new string('x', 262_145);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "session.jsonl"),
            oversizedLine + Environment.NewLine + CreateTokenCountLine(now.AddHours(-1)) + Environment.NewLine);

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));

            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(1000, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAFileThatExceedsTheCachedEventBudget()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        await using (var writer = new StreamWriter(file, append: false))
        {
            for (var index = 0; index < 100_001; index++)
            {
                await writer.WriteLineAsync(CreateTokenCountLine(now.AddMinutes(-1), 1));
            }
        }

        try
        {
            var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));

            await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetSnapshotAsync(CancellationToken.None));
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

    private static string CreateTokenCountLine(DateTimeOffset timestamp, long totalTokens = 1000)
        => JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    last_token_usage = new { total_tokens = totalTokens },
                },
            },
        });

    private static string CreateCumulativeTokenCountLine(DateTimeOffset timestamp, long totalTokens)
        => JsonSerializer.Serialize(new
        {
            timestamp,
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new { total_tokens = totalTokens },
                },
            },
        });
}
