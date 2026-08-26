using System.Globalization;
using System.Text.Json;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Services;

public interface ICodexUsageFallback
{
    Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class CodexLocalSessionFallback : ICodexUsageFallback
{
    private readonly IReadOnlyList<string> _codexHomes;
    private readonly long _fiveHourLimitTokens;
    private readonly long _weeklyLimitTokens;
    private readonly TimeProvider _timeProvider;

    public CodexLocalSessionFallback(
        string? codexHome = null,
        long fiveHourLimitTokens = 100_000,
        long weeklyLimitTokens = 500_000,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fiveHourLimitTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weeklyLimitTokens);

        _codexHomes = (codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _fiveHourLimitTokens = fiveHourLimitTokens;
        _weeklyLimitTokens = weeklyLimitTokens;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CodexUsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var fiveHourCutoff = now - TimeSpan.FromHours(5);
        var weeklyCutoff = now - TimeSpan.FromDays(7);
        long fiveHourTokens = 0;
        long weeklyTokens = 0;

        foreach (var home in _codexHomes)
        {
            foreach (var file in EnumerateSessionFiles(home))
            {
                var previousCumulative = 0L;
                await foreach (var line in File.ReadLinesAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    if (!TryReadTokenEvent(line, out var timestamp, out var delta, ref previousCumulative))
                    {
                        continue;
                    }

                    if (timestamp >= weeklyCutoff && timestamp <= now)
                    {
                        weeklyTokens += delta;
                    }

                    if (timestamp >= fiveHourCutoff && timestamp <= now)
                    {
                        fiveHourTokens += delta;
                    }
                }
            }
        }

        return new CodexUsageSnapshot(
            Percent(fiveHourTokens, _fiveHourLimitTokens),
            now.AddHours(5),
            Percent(weeklyTokens, _weeklyLimitTokens),
            now.AddDays(7),
            "local session estimate",
            true);
    }

    private static IEnumerable<string> EnumerateSessionFiles(string home)
    {
        if (!Directory.Exists(home))
        {
            yield break;
        }

        var directories = new[]
        {
            Path.Combine(home, "sessions"),
            Path.Combine(home, "archived_sessions"),
        };
        var foundDirectory = false;
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foundDirectory = true;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        if (!foundDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(home, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }
    }

    private static bool TryReadTokenEvent(
        string line,
        out DateTimeOffset timestamp,
        out long delta,
        ref long previousCumulative)
    {
        timestamp = default;
        delta = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!TryGetDateTimeOffset(root, "timestamp", out timestamp)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !string.Equals(GetString(payload, "type"), "token_count", StringComparison.OrdinalIgnoreCase)
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (info.TryGetProperty("last_token_usage", out var lastUsage)
                && TryGetLong(lastUsage, "total_tokens", out var lastDelta))
            {
                delta = Math.Max(0, lastDelta);
                return delta > 0;
            }

            if (info.TryGetProperty("total_token_usage", out var totalUsage)
                && TryGetLong(totalUsage, "total_tokens", out var cumulative))
            {
                delta = Math.Max(0, cumulative - previousCumulative);
                previousCumulative = Math.Max(previousCumulative, cumulative);
                return delta > 0;
            }
        }
        catch (JsonException)
        {
            // A partially-written JSONL line should not prevent the fallback from reading other sessions.
        }

        return false;
    }

    private static double Percent(long used, long limit) => Math.Clamp(used * 100d / limit, 0d, 100d);

    private static string? GetString(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetLong(JsonElement parent, string propertyName, out long value)
    {
        value = 0;
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        return element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDateTimeOffset(JsonElement parent, string propertyName, out DateTimeOffset value)
    {
        value = default;
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
            element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var unixSeconds))
        {
            value = unixSeconds > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixSeconds)
                : DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }

        return false;
    }
}
