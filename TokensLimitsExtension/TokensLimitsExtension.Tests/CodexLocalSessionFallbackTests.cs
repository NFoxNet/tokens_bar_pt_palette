using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Collections;
using Xunit.Abstractions;

namespace TokensLimitsExtension.Tests;

public sealed class CodexLocalSessionFallbackTests
{
    private readonly ITestOutputHelper _output;

    public CodexLocalSessionFallbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

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
            using var provider = new CodexLocalSessionFallback(
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
    public async Task ReportsInitialAndAppendReadResourceMeasurements()
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

            var initial = await MeasureAsync(
                () => provider.GetSnapshotAsync(CancellationToken.None),
                reads);
            reads.Clear();
            await File.AppendAllTextAsync(file, "}" + Environment.NewLine);
            var append = await MeasureAsync(
                () => provider.GetSnapshotAsync(CancellationToken.None),
                reads);

            Assert.True(initial.BytesRead > 300_000);
            Assert.True(append.BytesRead < initial.BytesRead);
            Assert.True(initial.Elapsed >= TimeSpan.Zero);
            Assert.True(append.Elapsed >= TimeSpan.Zero);
            Assert.Equal(302, (await provider.GetSnapshotAsync(CancellationToken.None))
                .Metrics.Single(metric => metric.SemanticKey == "tokens5h")
                .NumericValue);

            _output.WriteLine(
                $"Codex JSONL initial: bytes={initial.BytesRead}, elapsedMs={initial.Elapsed.TotalMilliseconds:F2}, " +
                $"allocatedBytes={initial.AllocatedBytes}, privateBytesDelta={initial.PrivateBytesDelta}");
            _output.WriteLine(
                $"Codex JSONL append: bytes={append.BytesRead}, elapsedMs={append.Elapsed.TotalMilliseconds:F2}, " +
                $"allocatedBytes={append.AllocatedBytes}, privateBytesDelta={append.PrivateBytesDelta}");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainsOnlyRollingWindowEventsWhenHistoricalEventsExceedTheBudget(bool includeRecentEvent)
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        await using (var writer = new StreamWriter(file, append: false))
        {
            var oldLine = CreateTokenCountLine(now.AddDays(-30), 1);
            for (var index = 0; index < 100_001; index++)
            {
                await writer.WriteLineAsync(oldLine);
            }

            if (includeRecentEvent)
            {
                await writer.WriteLineAsync(CreateTokenCountLine(now.AddMinutes(-1), 25));
            }
        }

        try
        {
            using var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);
            var unchanged = await provider.GetSnapshotAsync(CancellationToken.None);

            Assert.True(snapshot.IsEstimate);
            Assert.Equal(includeRecentEvent ? 25 : 0, snapshot.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(includeRecentEvent ? 25 : 0, unchanged.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(includeRecentEvent ? 1 : 0, GetRetainedEventCount(provider));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task EvictsExpiredEventsFromUnchangedFilesAndKeepsBoundaryAndFutureEvents()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllLinesAsync(Path.Combine(sessions, "session.jsonl"), [
            CreateTokenCountLine(now.AddDays(-7), 1),
            CreateTokenCountLine(now.AddDays(-7).AddTicks(-1), 2),
            CreateTokenCountLine(now.AddHours(-5), 4),
            CreateTokenCountLine(now.AddHours(-5).AddTicks(-1), 8),
            CreateTokenCountLine(now.AddMinutes(1), 16),
        ]);

        try
        {
            var clock = new MutableTimeProvider(now);
            var bytesRead = 0L;
            using var provider = new CodexLocalSessionFallback(home, timeProvider: clock, readBytesObserver: bytes => bytesRead += bytes);
            var initial = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(13, initial.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(4, initial.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(4, GetRetainedEventCount(provider));

            bytesRead = 0;
            clock.Now = now.AddMinutes(1);
            var advanced = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(28, advanced.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(16, advanced.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(3, GetRetainedEventCount(provider));
            Assert.Equal(0, bytesRead);

            clock.Now = now.AddDays(8);
            var expired = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(0, expired.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(0, GetRetainedEventCount(provider));
            Assert.Equal(0, bytesRead);

            clock.Now = now;
            var rewound = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(13, rewound.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(4, GetRetainedEventCount(provider));
            Assert.True(bytesRead > 0);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task KeepsCumulativeBaselineWhenExpiredEventsAreEvictedBeforeAppend()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(sessions, "session.jsonl");
        await File.WriteAllLinesAsync(file, [CreateCumulativeTokenCountLine(now.AddDays(-8), 100)]);

        try
        {
            using var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            var oldOnly = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(0, oldOnly.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(0, GetRetainedEventCount(provider));

            await File.AppendAllTextAsync(file, CreateCumulativeTokenCountLine(now.AddHours(-1), 150) + Environment.NewLine);
            var appended = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(50, appended.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(1, GetRetainedEventCount(provider));

            await File.AppendAllTextAsync(file, CreateCumulativeTokenCountLine(now, 20) + Environment.NewLine);
            var reset = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(70, reset.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(2, GetRetainedEventCount(provider));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task CompletesPartialCumulativeTailAfterEvictingHistoricalEvents()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(home, "sessions", "session.jsonl");
        var tail = CreateCumulativeTokenCountLine(now.AddHours(-1), 150);
        await File.WriteAllTextAsync(file, CreateCumulativeTokenCountLine(now.AddDays(-8), 100) + Environment.NewLine + tail[..^1]);

        try
        {
            using var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            var initial = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(0, initial.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(0, GetRetainedEventCount(provider));

            await File.AppendAllTextAsync(file, "}" + Environment.NewLine);
            var completed = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(50, completed.Metrics.Single(metric => metric.SemanticKey == "tokens5h").NumericValue);
            Assert.Equal(1, GetRetainedEventCount(provider));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReReadsReplacedOrTruncatedHistoryAndDropsDeletedFiles(bool truncate)
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(home, "sessions", "session.jsonl");
        var old = CreateCumulativeTokenCountLine(now.AddDays(-8), 100) + Environment.NewLine;
        await File.WriteAllTextAsync(file, truncate ? old + old : old);

        try
        {
            using var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now));
            await provider.GetSnapshotAsync(CancellationToken.None);
            var lastWrite = File.GetLastWriteTimeUtc(file);
            await File.WriteAllTextAsync(file, CreateCumulativeTokenCountLine(now.AddHours(-1), 200) + Environment.NewLine);
            File.SetLastWriteTimeUtc(file, lastWrite.AddSeconds(2));
            var replaced = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(200, replaced.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(1, GetRetainedEventCount(provider));

            File.Delete(file);
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetSnapshotAsync(CancellationToken.None));
            Assert.Equal(0, GetRetainedEventCount(provider));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledAppendDoesNotPublishPartiallyReadEventsOrCumulativeState()
    {
        var home = Path.Combine(Path.GetTempPath(), $"codex-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var file = Path.Combine(home, "sessions", "session.jsonl");
        await File.WriteAllLinesAsync(file, [CreateCumulativeTokenCountLine(now.AddHours(-2), 100)]);

        try
        {
            using var cancellation = new CancellationTokenSource();
            var cancelOnRead = false;
            using var provider = new CodexLocalSessionFallback(home, timeProvider: new FixedTimeProvider(now), readBytesObserver: _ =>
            {
                if (cancelOnRead)
                {
                    cancellation.Cancel();
                }
            });
            await provider.GetSnapshotAsync(CancellationToken.None);
            await File.AppendAllTextAsync(file, CreateCumulativeTokenCountLine(now.AddHours(-1), 150) + Environment.NewLine);
            cancelOnRead = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetSnapshotAsync(cancellation.Token));
            Assert.Equal(1, GetRetainedEventCount(provider));

            cancelOnRead = false;
            var retried = await provider.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(150, retried.Metrics.Single(metric => metric.SemanticKey == "tokens7d").NumericValue);
            Assert.Equal(2, GetRetainedEventCount(provider));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static int GetRetainedEventCount(CodexLocalSessionFallback provider)
    {
        // Inspect retention without exposing a production API solely for resource tests.
        var cache = typeof(CodexLocalSessionFallback).GetField("_fileCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider)!;
        var values = (IEnumerable)cache.GetType().GetProperty("Values")!.GetValue(cache)!;
        return values.Cast<object>().Sum(value => ((IEnumerable)value.GetType().GetProperty("Events")!.GetValue(value)!)
            .Cast<object>().Count());
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
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

    private static async Task<ReadMeasurement> MeasureAsync(
        Func<Task<CodexUsageSnapshot>> operation,
        IReadOnlyList<long> reads)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var privateBytesBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        var stopwatch = Stopwatch.StartNew();
        await operation();
        stopwatch.Stop();
        var allocatedAfter = GC.GetTotalAllocatedBytes(false);
        var privateBytesAfter = Process.GetCurrentProcess().PrivateMemorySize64;
        return new ReadMeasurement(
            reads.Sum(),
            stopwatch.Elapsed,
            Math.Max(0, allocatedAfter - allocatedBefore),
            privateBytesAfter - privateBytesBefore);
    }

    private readonly record struct ReadMeasurement(
        long BytesRead,
        TimeSpan Elapsed,
        long AllocatedBytes,
        long PrivateBytesDelta);
}
