using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Runs and parses the Kiro CLI usage report. Keeping the process boundary and
/// text parser together prevents the catalog-backed provider from accumulating
/// another provider-specific transport branch.
/// </summary>
internal static partial class KiroUsageProviderAdapter
{
    public static async Task<UsageSnapshot> GetSnapshotAsync(
        UsageProviderDescriptor descriptor,
        IUsageProviderConfiguration configuration,
        IUsageProviderProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var configuredPath = configuration.GetValue(descriptor.Id, "cliPath");
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? "kiro-cli.exe" : configuredPath;
        var result = await processRunner.RunAsync(
            executable,
            ["chat", "--no-interactive", "/usage"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? "kiro-cli завершился с ошибкой."
                : result.StandardError.Trim();
            throw new UsageProviderRequestException($"Kiro: {error}");
        }

        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return ParseUsage(output, DateTimeOffset.UtcNow);
    }

    private static UsageSnapshot ParseUsage(string output, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new UsageProviderRequestException("Kiro: kiro-cli не вернул отчёт об использовании.");
        }

        var normalized = StripAnsi(output);
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("not logged in", StringComparison.Ordinal)
            || lower.Contains("login required", StringComparison.Ordinal)
            || lower.Contains("kiro-cli login", StringComparison.Ordinal))
        {
            throw new UsageProviderConfigurationException(
                "Kiro не авторизован. Выполните kiro-cli login штатным способом.");
        }

        var plan = Regex.Match(normalized, @"(?im)^\s*(?:plan|subscription)\s*:\s*(?<value>[^\r\n]+)")
            .Groups["value"].Value.Trim();
        var creditMatch = Regex.Match(
            normalized,
            @"\((?<used>\d+(?:[.,]\d+)?)\s+of\s+(?<total>\d+(?:[.,]\d+)?)\s+covered",
            RegexOptions.IgnoreCase);
        double? used = creditMatch.Success ? ParseFlexibleNumber(creditMatch.Groups["used"].Value) : null;
        double? total = creditMatch.Success ? ParseFlexibleNumber(creditMatch.Groups["total"].Value) : null;

        var percentMatch = Regex.Match(normalized, @"█+\s*(?<percent>\d+(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (!percentMatch.Success)
        {
            percentMatch = Regex.Match(normalized, @"(?i)(?:credits|usage)[^\r\n]{0,80}(?<percent>\d+(?:[.,]\d+)?)\s*%\s*used");
        }

        double? usedPercent = percentMatch.Success
            ? ParseFlexibleNumber(percentMatch.Groups["percent"].Value)
            : null;
        if (usedPercent is null && used is not null && total is > 0)
        {
            usedPercent = used.Value / total.Value * 100;
        }

        var resetAt = TryParseReset(normalized, fetchedAt);
        var metrics = new List<UsageMetric>();
        if (!string.IsNullOrWhiteSpace(plan))
        {
            metrics.Add(new UsageMetric("Plan", plan));
        }

        if (used is not null)
        {
            metrics.Add(new UsageMetric("Credits used", FormatNumber(Math.Max(0, used.Value)), "credits", Used: used));
        }

        if (total is not null)
        {
            metrics.Add(new UsageMetric("Credits total", FormatNumber(Math.Max(0, total.Value)), "credits", Limit: total));
        }

        var bonus = Regex.Match(
            normalized,
            @"(?i)bonus[^\r\n]{0,80}(?<value>\d+(?:[.,]\d+)?)\s*(?:credits?)?");
        if (bonus.Success)
        {
            metrics.Add(new UsageMetric("Bonus credits", FormatNumber(ParseFlexibleNumber(bonus.Groups["value"].Value))));
        }

        if (usedPercent is null && metrics.Count == 0)
        {
            throw new UsageProviderRequestException(
                "Kiro: формат вывода kiro-cli не содержит распознаваемых данных использования.");
        }

        UsageWindow? primary = null;
        if (usedPercent is not null && resetAt is not null)
        {
            primary = new UsageWindow(Math.Clamp(usedPercent.Value, 0, 100), resetAt.Value, 0);
        }

        return new UsageSnapshot(
            "kiro",
            "Kiro",
            primary,
            null,
            string.IsNullOrWhiteSpace(plan) ? null : plan,
            false)
        {
            FetchedAt = fetchedAt,
            Source = "kiro-cli chat --no-interactive /usage",
            Metrics = metrics,
        };
    }

    private static DateTimeOffset? TryParseReset(string text, DateTimeOffset now)
    {
        var iso = Regex.Match(
            text,
            @"(?i)(?:reset|renew|next)[^\r\n]{0,80}(?<date>20\d{2}-\d{2}-\d{2}(?:[T ][0-9:.+\-Z]+)?)");
        if (iso.Success && DateTimeOffset.TryParse(
                iso.Groups["date"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        var relative = Regex.Match(
            text,
            @"(?i)(?:reset|renew|next)[^\r\n]{0,40}(?:(?<days>\d+)\s*d)?\s*(?:(?<hours>\d+)\s*h)?\s*(?:(?<minutes>\d+)\s*m)?");
        if (!relative.Success
            || (!relative.Groups["days"].Success && !relative.Groups["hours"].Success && !relative.Groups["minutes"].Success))
        {
            return null;
        }

        var days = ParseInteger(relative.Groups["days"].Value);
        var hours = ParseInteger(relative.Groups["hours"].Value);
        var minutes = ParseInteger(relative.Groups["minutes"].Value);
        return now.AddDays(days).AddHours(hours).AddMinutes(minutes);
    }

    private static double ParseFlexibleNumber(string raw)
        => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static int ParseInteger(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string StripAnsi(string value)
        => AnsiEscapeRegex().Replace(value, string.Empty);

    private static string FormatNumber(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();
}
