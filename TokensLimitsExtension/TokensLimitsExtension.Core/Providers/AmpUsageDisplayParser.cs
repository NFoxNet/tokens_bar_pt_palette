using System.Globalization;
using System.Text.RegularExpressions;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

/// <summary>Parses the human-readable usage text returned by Amp.</summary>
internal static class AmpUsageDisplayParser
{
    public static UsageSnapshot Parse(
        UsageProviderDescriptor descriptor,
        string text,
        DateTimeOffset now,
        string source)
    {
        var metrics = new List<UsageMetric>();
        UsageWindow? primary = null;
        UsageWindow? secondary = null;

        var freePercent = MatchNumber(text, @"Amp\s+Free:\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+remaining");
        var freeAmount = MatchNumbers(text, @"Amp\s+Free:\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s*/\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining(?:\s*\(replenishes\s*\+\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s*/\s*hour\))?");
        if (freeAmount.Count >= 2)
        {
            var remaining = freeAmount[0];
            var quota = freeAmount[1];
            var replenishment = freeAmount.Count > 2 ? freeAmount[2] : 0;
            metrics.Add(new UsageMetric("Free remaining", FormatNumber(remaining), "USD", Used: Math.Max(0, quota - remaining), Limit: quota, Remaining: remaining));
            if (quota > 0 && replenishment > 0)
            {
                var hours = Math.Max(1, quota / replenishment);
                primary = new UsageWindow(Math.Clamp((quota - remaining) / quota * 100, 0, 100), now.AddHours(hours), (int)Math.Round(hours * 60 * 60));
            }
        }
        else if (freePercent is not null)
        {
            metrics.Add(new UsageMetric("Free remaining", FormatNumber(freePercent.Value), "%", Remaining: freePercent.Value));
            var reset = now.UtcDateTime.Date.AddDays(1);
            primary = new UsageWindow(Math.Clamp(100 - freePercent.Value, 0, 100), new DateTimeOffset(reset, TimeSpan.Zero), 24 * 60 * 60);
        }

        var subscription = Regex.Match(
            text,
            @"(?im)^\s*(?:Subscription\s+(.+?):|Amp\s+(.+?)\s+Subscription:)\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+other\s+usage\s+and\s+([0-9][0-9,]*(?:\.[0-9]+)?)\s*%\s+orb\s+usage\s+remaining\s*-\s*resets\s+upon\s+renewal\s+in\s+([0-9][0-9,]*)\s+(days?|months?)");
        if (subscription.Success)
        {
            var plan = !string.IsNullOrWhiteSpace(subscription.Groups[1].Value)
                ? subscription.Groups[1].Value
                : subscription.Groups[2].Value;
            var otherRemaining = ParseInvariantNumber(subscription.Groups[3].Value);
            var orbRemaining = ParseInvariantNumber(subscription.Groups[4].Value);
            var resetValue = int.TryParse(subscription.Groups[5].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedReset)
                ? parsedReset
                : 0;
            var resetAt = subscription.Groups[6].Value.StartsWith("month", StringComparison.OrdinalIgnoreCase)
                ? now.AddMonths(resetValue)
                : now.AddDays(resetValue);
            metrics.Add(new UsageMetric("Plan", plan));
            metrics.Add(new UsageMetric("Subscription other", FormatNumber(otherRemaining), "%", Used: 100 - otherRemaining, Remaining: otherRemaining, ResetAt: resetAt));
            metrics.Add(new UsageMetric("Subscription orb", FormatNumber(orbRemaining), "%", Used: 100 - orbRemaining, Remaining: orbRemaining, ResetAt: resetAt));
            secondary = new UsageWindow(Math.Clamp(100 - otherRemaining, 0, 100), resetAt, 0);
        }

        var individualCredits = MatchNumber(text, @"Individual\s+credits:\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining");
        if (individualCredits is not null)
        {
            metrics.Add(new UsageMetric("Individual credits", FormatNumber(individualCredits.Value), "USD", Remaining: individualCredits.Value));
        }

        foreach (Match workspace in Regex.Matches(text, @"(?im)^\s*Workspace\s+(.+?):\s*\$?([0-9][0-9,]*(?:\.[0-9]+)?)\s+remaining"))
        {
            var remaining = ParseInvariantNumber(workspace.Groups[2].Value);
            metrics.Add(new UsageMetric($"Workspace {workspace.Groups[1].Value.Trim()}", FormatNumber(remaining), "USD", Remaining: remaining));
        }

        if (metrics.Count == 0)
        {
            throw new UsageProviderRequestException("Ответ Amp не содержит распознаваемых данных usage.");
        }

        return new UsageSnapshot(descriptor.Id, descriptor.DisplayName, primary, secondary, null, false)
        {
            FetchedAt = now,
            Source = source,
            Metrics = metrics,
        };
    }

    private static double? MatchNumber(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? ParseInvariantNumber(match.Groups[1].Value) : null;
    }

    private static List<double> MatchNumbers(string text, string pattern)
        => Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .SelectMany(match => match.Groups.Cast<Group>().Skip(1).Where(group => group.Success).Select(group => ParseInvariantNumber(group.Value)))
            .ToList();

    private static double ParseInvariantNumber(string value)
        => double.TryParse(value.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string FormatNumber(double value)
        => value.ToString(value == Math.Truncate(value) ? "0" : "0.##", CultureInfo.InvariantCulture);
}
