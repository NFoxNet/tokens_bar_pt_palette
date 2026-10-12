using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Services;

namespace TokensLimitsExtension.Tests;

public sealed class UsageHistoryStoreTests
{
    private static readonly JsonSerializerOptions SeedJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void RoundTripsOnlyNormalizedNumbersAndApprovedMetricKeys()
    {
        var directory = NewDirectory();
        try
        {
            var entry = Entry(DateTimeOffset.UtcNow);
            var store = new UsageHistoryStore(directory);

            store.Append(entry);

            var restored = Assert.Single(store.Read("codex"));
            Assert.Equal(entry.Timestamp, restored.Timestamp);
            Assert.Equal(entry.PrimaryWindow, restored.PrimaryWindow);
            Assert.Equal(entry.Metrics, restored.Metrics);
            var json = File.ReadAllText(HistoryPath(directory, "codex"));
            Assert.DoesNotContain("provider secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw metric label", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw metric value", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("session path", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DropsSamplesCloserThanFiveMinutesForTheSameProvider()
    {
        var directory = NewDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new UsageHistoryStore(directory);
            store.Append(Entry(now.AddMinutes(-4)));
            store.Append(Entry(now));

            Assert.Single(store.Read("codex"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptHistoryIsReportedAndAppendDoesNotOverwriteIt()
    {
        var directory = NewDirectory();
        try
        {
            var path = HistoryPath(directory, "codex");
            File.WriteAllText(path, "{broken");
            var original = File.ReadAllBytes(path);
            var store = new UsageHistoryStore(directory);

            Assert.Throws<UsageHistoryException>(() => store.Read("codex"));
            Assert.Throws<UsageHistoryException>(() => store.Append(Entry(DateTimeOffset.UtcNow)));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownWindowEvidenceIsNeverForecastEligible()
    {
        var entry = Entry(DateTimeOffset.UtcNow) with
        {
            PrimaryWindow = new UsageHistoryWindow(20, null, DateTimeOffset.UtcNow.AddHours(1), 3600, false),
        };

        Assert.False(entry.PrimaryWindow.IsForecastEligible);
    }

    [Fact]
    public void SnapshotAccountScopeIsNeverSerialized()
    {
        var snapshot = new UsageSnapshot("codex", "Codex", null, null, null, false)
        {
            AccountScope = "opaque-account-hash",
        };

        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("opaque-account-hash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountScope", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnapprovedMetricKeysInsteadOfPersistingUserText()
    {
        var directory = NewDirectory();
        try
        {
            var entry = Entry(DateTimeOffset.UtcNow) with
            {
                Metrics = [new UsageHistoryMetric("raw metric label", NumericValue: 123)],
            };

            Assert.Throws<UsageHistoryException>(() => new UsageHistoryStore(directory).Append(entry));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RetentionCanBeChangedAndExpiredSamplesAreNotReintroduced()
    {
        var directory = NewDirectory();
        try
        {
            var store = new UsageHistoryStore(directory);
            store.SetRetentionDays(7);

            Assert.False(store.Append(Entry(DateTimeOffset.UtcNow.AddDays(-8))));
            Assert.Empty(store.Read("codex"));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SetRetentionDays(8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void KeepsAtMost8640EntriesPerProvider()
    {
        var directory = NewDirectory();
        try
        {
            var clock = new TestTimeProvider(DateTimeOffset.UtcNow.AddDays(1));
            var now = clock.GetUtcNow();
            var seeded = Enumerable.Range(0, UsageHistoryStore.MaximumEntriesPerProvider)
                .Select(index => Entry(now.AddMinutes(-5 * (UsageHistoryStore.MaximumEntriesPerProvider - index - 1))))
                .ToArray();
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                HistoryPath(directory, "codex"),
                JsonSerializer.SerializeToUtf8Bytes(seeded, SeedJsonOptions));
            var store = new UsageHistoryStore(directory, 30, clock);

            Assert.True(store.Append(Entry(now.AddMinutes(5))));

            Assert.Equal(UsageHistoryStore.MaximumEntriesPerProvider, store.Read("codex").Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OversizedHistoryIsReportedAndCannotBeOverwritten()
    {
        var directory = NewDirectory();
        try
        {
            var path = HistoryPath(directory, "codex");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(path, FileMode.CreateNew))
            {
                stream.SetLength(UsageHistoryStore.MaximumFileBytes + 1L);
            }

            var store = new UsageHistoryStore(directory);

            var exception = Assert.Throws<UsageHistoryException>(() => store.Append(Entry(DateTimeOffset.UtcNow)));
            Assert.Equal(UsageHistoryErrorKind.TooLarge, exception.Kind);
            Assert.Equal(UsageHistoryStore.MaximumFileBytes + 1L, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClearRemovesHistoryForAllProviders()
    {
        var directory = NewDirectory();
        try
        {
            var store = new UsageHistoryStore(directory);
            store.Append(Entry(DateTimeOffset.UtcNow));
            store.Append(Entry(DateTimeOffset.UtcNow) with { ProviderId = "openai" });

            store.Clear();

            Assert.Empty(store.Read("codex"));
            Assert.Empty(store.Read("openai"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsFarFutureTimestampAsTypedErrorWithoutOverwritingHistory()
    {
        var directory = NewDirectory();
        try
        {
            var store = new UsageHistoryStore(directory);
            store.Append(Entry(DateTimeOffset.UtcNow));
            var path = HistoryPath(directory, "codex");
            var original = File.ReadAllBytes(path);

            var exception = Assert.Throws<UsageHistoryException>(() => store.Append(Entry(DateTimeOffset.UtcNow) with { Timestamp = DateTimeOffset.MaxValue }));

            Assert.Equal(UsageHistoryErrorKind.InvalidData, exception.Kind);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    private static UsageHistoryEntry Entry(DateTimeOffset timestamp)
        => new(
            timestamp,
            "codex",
            UsageHistorySourceKind.Api,
            Guid.Parse("be8b6d30-282c-4e20-a2d9-27bd37ca1c95"),
            false,
            new UsageHistoryWindow(20, timestamp.AddHours(-1), timestamp.AddHours(4), 18000, true),
            null,
            [new UsageHistoryMetric("tokens5h", NumericValue: 123)]);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string HistoryPath(string directory, string providerId)
        => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId))) + ".json");

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tokens-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
