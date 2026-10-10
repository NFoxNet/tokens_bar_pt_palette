using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public enum UsageHistoryErrorKind
{
    Corrupt,
    TooLarge,
    InvalidData,
    Unavailable,
}

public sealed class UsageHistoryException(UsageHistoryErrorKind kind, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public UsageHistoryErrorKind Kind { get; } = kind;
}

public sealed class UsageHistoryStore
{
    /// <summary>Maximum retained samples for one provider.</summary>
    public const int MaximumEntriesPerProvider = 8_640;

    /// <summary>Maximum serialized size read or written for one provider history file.</summary>
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    private const int MinimumSampleIntervalMinutes = 5;
    private const int MaximumMetricsPerEntry = 32;
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    private static readonly object FileGate = new();
    private static readonly HashSet<string> AllowedMetricKeys = new(StringComparer.Ordinal)
    {
        "tokens5h",
        "tokens7d",
        "balance",
        "totalBalance",
        "quotaUnavailable",
        "connectionStatus",
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private int _retentionDays;

    public UsageHistoryStore(string directory, int retentionDays = 14, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (retentionDays is not (7 or 14 or 30))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 7, 14, or 30 days.");
        }

        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retentionDays = retentionDays;
    }

    public void SetRetentionDays(int retentionDays)
    {
        if (retentionDays is not (7 or 14 or 30))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention must be 7, 14, or 30 days.");
        }

        lock (FileGate)
        {
            _retentionDays = retentionDays;
        }
    }

    public bool Append(UsageHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = GetFilePath(entry.ProviderId);
        ValidateEntry(entry, entry.ProviderId, _timeProvider.GetUtcNow());

        lock (FileGate)
        {
            var now = _timeProvider.GetUtcNow();
            var entries = ReadFile(path, entry.ProviderId, now);
            entries.RemoveAll(item => item.Timestamp < now.AddDays(-_retentionDays));
            if (entry.Timestamp < now.AddDays(-_retentionDays))
            {
                return false;
            }

            var latest = entries.Count == 0 ? (DateTimeOffset?)null : entries.Max(item => item.Timestamp);
            if (latest is { } last && (entry.Timestamp - last) < TimeSpan.FromMinutes(MinimumSampleIntervalMinutes))
            {
                return false;
            }

            entries.Add(entry);
            entries.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
            if (entries.Count > MaximumEntriesPerProvider)
            {
                entries.RemoveRange(0, entries.Count - MaximumEntriesPerProvider);
            }

            WriteFileAtomically(path, entries);
            return true;
        }
    }

    public IReadOnlyList<UsageHistoryEntry> Read(string providerId)
    {
        var path = GetFilePath(providerId);
        lock (FileGate)
        {
            var now = _timeProvider.GetUtcNow();
            var cutoff = now.AddDays(-_retentionDays);
            return ReadFile(path, providerId, now)
                .Where(entry => entry.Timestamp >= cutoff)
                .OrderBy(entry => entry.Timestamp)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (FileGate)
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.Length == 64 && fileName.All(Uri.IsHexDigit))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private string GetFilePath(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128
            || providerId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("Provider ID is invalid.", nameof(providerId));
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId)));
        return Path.Combine(_directory, hash + ".json");
    }

    private static void ValidateEntry(UsageHistoryEntry entry, string expectedProviderId, DateTimeOffset now)
    {
        if (entry.Timestamp.UtcTicks > now.UtcTicks
                && entry.Timestamp.UtcTicks - now.UtcTicks > MaximumFutureClockSkew.Ticks
            || !string.Equals(entry.ProviderId, expectedProviderId, StringComparison.Ordinal)
            || entry.SegmentId == Guid.Empty || !Enum.IsDefined(entry.SourceKind))
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.InvalidData, "History entry is invalid.");
        }

        if (entry.Metrics is null || entry.Metrics.Count > MaximumMetricsPerEntry
            || entry.Metrics.Any(metric => metric is null || !AllowedMetricKeys.Contains(metric.SemanticKey)
                || !IsFinite(metric.Used) || !IsFinite(metric.Limit) || !IsFinite(metric.Remaining)))
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.InvalidData, "History metrics are invalid.");
        }

        ValidateWindow(entry.PrimaryWindow);
        ValidateWindow(entry.SecondaryWindow);
    }

    private static void ValidateWindow(UsageHistoryWindow? window)
    {
        if (window is not null && (!double.IsFinite(window.UsedPercent)
            || window.UsedPercent is < 0 or > 100
            || window.LimitWindowSeconds < 0))
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.InvalidData, "History window is invalid.");
        }
    }

    private static bool IsFinite(double? value) => value is null || double.IsFinite(value.Value);

    private static List<UsageHistoryEntry> ReadFile(string path, string providerId, DateTimeOffset now)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes)
            {
                throw new UsageHistoryException(UsageHistoryErrorKind.TooLarge, "History file exceeds the allowed size.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (UsageHistoryException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.Corrupt, "History file could not be read.", exception);
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<UsageHistoryEntry>>(bytes, JsonOptions)
                ?? throw new JsonException("History root is empty.");
            if (entries.Count > MaximumEntriesPerProvider || entries.Any(entry => entry is null))
            {
                throw new UsageHistoryException(UsageHistoryErrorKind.InvalidData, "History file contains invalid data.");
            }

            foreach (var entry in entries)
            {
                ValidateEntry(entry, providerId, now);
            }

            return entries;
        }
        catch (UsageHistoryException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.Corrupt, "History file is invalid.", exception);
        }
    }

    private void WriteFileAtomically(string path, IReadOnlyList<UsageHistoryEntry> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
        {
            throw new UsageHistoryException(UsageHistoryErrorKind.TooLarge, "History file exceeds the allowed size.");
        }

        Directory.CreateDirectory(_directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

