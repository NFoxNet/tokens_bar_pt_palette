using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

internal static class UsageJsonParser
{
    private static readonly string[] InterestingWords =
    [
        "usage", "used", "limit", "remaining", "quota", "credit", "balance", "token", "cost",
        "spend", "request", "character", "point", "plan", "reset", "refill", "budget", "amount",
        "total", "model",
    ];

    public static UsageSnapshot Parse(
        UsageProviderDescriptor descriptor,
        string source,
        JsonElement root,
        DateTimeOffset fetchedAt)
    {
        if (descriptor.Id.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            && TryParseDeepSeekBalance(descriptor, source, root, fetchedAt, out var deepSeekSnapshot))
        {
            return deepSeekSnapshot;
        }

        var leaves = new List<(string Path, JsonElement Value)>();
        CollectLeaves(root, string.Empty, leaves, 0);
        var metrics = new List<UsageMetric>();
        foreach (var leaf in leaves)
        {
            if (!IsInteresting(leaf.Path, leaf.Value))
            {
                continue;
            }

            var value = FormatValue(leaf.Value);
            if (value is null)
            {
                continue;
            }

            metrics.Add(new UsageMetric(PrettyName(leaf.Path), value));
            if (metrics.Count >= 32)
            {
                break;
            }
        }

        if (descriptor.Id.Equals("azureopenai", StringComparison.OrdinalIgnoreCase))
        {
            var model = FindString(root, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                metrics.Add(new UsageMetric("Model", model));
            }
        }

        var windows = FindWindows(descriptor, root, fetchedAt);
        var plan = FindString(
            root,
            "plan",
            "planName",
            "plan_name",
            "tier",
            "subscription",
            "product",
            "displayName",
            "planId",
            "current_subscribe_title",
            "current_plan_title",
            "combo_title",
            "packageName");
        if (windows.Primary is null && windows.Secondary is null && metrics.Count == 0)
        {
            throw new UsageProviderRequestException(
                $"Ответ {descriptor.DisplayName} не содержит распознаваемых лимитов или метрик.");
        }

        return new UsageSnapshot(
            descriptor.Id,
            descriptor.DisplayName,
            windows.Primary,
            windows.Secondary,
            plan,
            false)
        {
            FetchedAt = fetchedAt,
            Source = source,
            Metrics = metrics,
        };
    }

    public static UsageSnapshot ParseOllama(
        UsageProviderDescriptor descriptor,
        JsonElement root,
        DateTimeOffset fetchedAt)
    {
        if (!root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new UsageProviderRequestException(
                "Ответ Ollama не содержит списка локальных моделей.");
        }

        var modelNames = models
            .EnumerateArray()
            .Where(model => model.ValueKind == JsonValueKind.Object)
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        var metrics = new List<UsageMetric>
        {
            new("Models", modelNames.Length.ToString(CultureInfo.InvariantCulture), "models"),
        };
        metrics.AddRange(modelNames.Take(16).Select((name, index) =>
            new UsageMetric($"Model {index + 1}", name)));

        return new UsageSnapshot(
            descriptor.Id,
            descriptor.DisplayName,
            null,
            null,
            "Локальный Ollama",
            false)
        {
            FetchedAt = fetchedAt,
            Source = "http://127.0.0.1:11434/api/tags",
            Metrics = metrics,
        };
    }

    public static UsageSnapshot ParseText(
        UsageProviderDescriptor descriptor,
        string source,
        string raw,
        DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new UsageProviderRequestException(
                $"Ответ {descriptor.DisplayName} пуст.");
        }

        var normalized = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (normalized.StartsWith(")]}'", StringComparison.Ordinal))
        {
            var newline = normalized.IndexOf('\n');
            normalized = newline >= 0 ? normalized[(newline + 1)..] : normalized;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            return Parse(descriptor, source, document.RootElement, fetchedAt);
        }
        catch (JsonException jsonException)
        {
            foreach (var line in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var lineDocument = JsonDocument.Parse(line);
                    return Parse(descriptor, source, lineDocument.RootElement, fetchedAt);
                }
                catch (JsonException)
                {
                }
                catch (UsageProviderRequestException)
                {
                }
            }

            try
            {
                return ParseXml(descriptor, source, normalized, fetchedAt);
            }
            catch (UsageProviderRequestException)
            {
                throw jsonException;
            }
        }
    }

    public static UsageSnapshot Merge(
        UsageProviderDescriptor descriptor,
        IReadOnlyList<UsageSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("At least one snapshot is required.", nameof(snapshots));
        }

        if (snapshots.Count == 1)
        {
            return snapshots[0];
        }

        var metrics = snapshots
            .SelectMany(snapshot => snapshot.Metrics)
            .GroupBy(metric => new
            {
                metric.Name,
                metric.Value,
                metric.Unit,
                metric.Used,
                metric.Limit,
                metric.Remaining,
                metric.ResetAt,
                metric.SemanticKey,
                metric.NumericValue,
                metric.CurrencyCode,
            })
            .Select(group => group.First())
            .Take(64)
            .ToArray();
        var additional = snapshots
            .SelectMany(snapshot => snapshot.AdditionalRateLimits)
            .GroupBy(limit => limit.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var sources = snapshots
            .Select(snapshot => snapshot.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new UsageSnapshot(
            descriptor.Id,
            descriptor.DisplayName,
            snapshots.Select(snapshot => snapshot.PrimaryWindow).FirstOrDefault(window => window is not null),
            snapshots.Select(snapshot => snapshot.SecondaryWindow).FirstOrDefault(window => window is not null),
            snapshots.Select(snapshot => snapshot.Plan).FirstOrDefault(plan => !string.IsNullOrWhiteSpace(plan)),
            snapshots.Any(snapshot => snapshot.IsEstimate))
        {
            AdditionalRateLimits = additional,
            Metrics = metrics,
            FetchedAt = snapshots.Max(snapshot => snapshot.FetchedAt ?? DateTimeOffset.MinValue),
            Source = string.Join(", ", sources),
        };
    }

    public static UsageSnapshot ParseXml(
        UsageProviderDescriptor descriptor,
        string source,
        string raw,
        DateTimeOffset fetchedAt)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(raw, LoadOptions.None);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new UsageProviderRequestException(
                $"Ответ {descriptor.DisplayName} не является JSON/XML с метриками.",
                ex);
        }

        var metrics = document
            .Descendants()
            .SelectMany(element => element.Attributes()
                .Select(attribute => new KeyValuePair<string, string>(attribute.Name.LocalName, attribute.Value))
                .Append(new KeyValuePair<string, string>(element.Name.LocalName, element.Value.Trim())))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                && InterestingWords.Any(word => pair.Key.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Take(32)
            .Select(pair => new UsageMetric(PrettyName(pair.Key), pair.Value))
            .ToArray();
        if (metrics.Length == 0)
        {
            throw new UsageProviderRequestException(
                $"Ответ {descriptor.DisplayName} не содержит распознаваемых локальных метрик.");
        }

        return new UsageSnapshot(
            descriptor.Id,
            descriptor.DisplayName,
            null,
            null,
            null,
            false)
        {
            FetchedAt = fetchedAt,
            Source = source,
            Metrics = metrics,
        };
    }

    private static (UsageWindow? Primary, UsageWindow? Secondary) FindWindows(
        UsageProviderDescriptor descriptor,
        JsonElement root,
        DateTimeOffset fetchedAt)
    {
        var special = FindProviderSpecificWindows(descriptor.Id, root);
        if (special.Found)
        {
            return (special.Primary, special.Secondary);
        }

        var candidates = new List<UsageWindowCandidate>();
        CollectObjects(root, string.Empty, candidates, fetchedAt, 0);
        var primaryCandidate = candidates.FirstOrDefault(candidate => candidate.Kind == WindowKind.Primary);
        var secondaryCandidate = candidates.FirstOrDefault(candidate => candidate.Kind == WindowKind.Secondary);
        if (primaryCandidate is null && secondaryCandidate is null)
        {
            primaryCandidate = candidates.FirstOrDefault();
        }
        else if (primaryCandidate is not null && (secondaryCandidate is null || ReferenceEquals(primaryCandidate, secondaryCandidate)))
        {
            secondaryCandidate = candidates.FirstOrDefault(candidate => !ReferenceEquals(candidate, primaryCandidate));
        }
        return (primaryCandidate?.Window, secondaryCandidate?.Window);
    }

    private static void CollectObjects(
        JsonElement element,
        string path,
        List<UsageWindowCandidate> candidates,
        DateTimeOffset fetchedAt,
        int depth,
        DateTimeOffset? inheritedReset = null)
    {
        if (depth > 8)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            var used = FindNumber(properties, "used_percent", "usedPercent", "usage_percent", "usagePercent", "percentage_used", "percentUsed", "percentage");
            var utilization = FindNumber(properties, "utilization", "utilization_percent", "usage_percentage", "usagePercentage");
            var remaining = FindNumber(
                properties,
                "remaining_percent",
                "remainingPercent",
                "percentage_remaining",
                "percentRemaining",
                "currentIntervalRemainingPercent",
                "current_interval_remaining_percent",
                "currentWeeklyRemainingPercent",
                "current_weekly_remaining_percent",
                "remainingValue",
                "remaining_value",
                "availableToken",
                "available_token");
            var limit = FindNumber(
                properties,
                "limit",
                "limitValue",
                "limit_value",
                "quota",
                "max",
                "maximum",
                "total",
                "capacity",
                "allowance",
                "grant_amount",
                "total_granted",
                "cap",
                "tokenLimit",
                "weeklyLimit",
                "currentIntervalTotalCount",
                "current_interval_total_count",
                "currentWeeklyTotalCount",
                "current_weekly_total_count",
                "currentIntervalLimit",
                "currentWeeklyLimit");
            var amountUsed = FindNumber(
                properties,
                "used",
                "usage",
                "consumed",
                "current",
                "used_amount",
                "total_used",
                "currentValue",
                "usedValue",
                "used_value",
                "usedToken",
                "consumedToken",
                "weeklyUsed",
                "currentIntervalUsageCount",
                "current_interval_usage_count",
                "currentWeeklyUsageCount",
                "current_weekly_usage_count");
            if (FindNumber(properties, "currentValue") is { } zAiCurrent
                && FindNumber(properties, "usage") is { } zAiUsage)
            {
                limit = zAiUsage;
                amountUsed = zAiCurrent;
            }

            var reset = FindDate(
                properties,
                "reset_at",
                "resetAt",
                "reset",
                "next_reset",
                "nextReset",
                "next_reset_at",
                "nextResetAt",
                "refill_at",
                "refillAt",
                "resetsAt",
                "expires_at",
                "expiration",
                "nextRefreshTime",
                "nextResetTime",
                "resetTime",
                "next_quota_reset",
                "nextQuotaReset",
                "currentIntervalResetAt",
                "currentWeeklyResetAt",
                "weeklyResetsAt",
                "endTime",
                "end_time",
                "weeklyEndTime",
                "weekly_end_time",
                "currentPeriodEnd",
                "billingCycleEnd",
                "billing_cycle_end",
                "dailyQuotaResetAtUnix",
                "weeklyQuotaResetAtUnix",
                "quotaResetDate");
            var effectiveReset = reset ?? inheritedReset;
            if (effectiveReset is null)
            {
                var resetInSeconds = FindNumber(
                    properties,
                    "resetInSec",
                    "resetInSeconds",
                    "resetSeconds",
                    "reset_sec",
                    "reset_in_sec",
                    "resetsInSec",
                    "resetsInSeconds");
                if (resetInSeconds is > 0)
                {
                    effectiveReset = fetchedAt.AddSeconds(resetInSeconds.Value);
                }
            }
            var t3FourHour = FindNumber(properties, "usageFourHourPercentage");
            var t3Monthly = FindNumber(properties, "usageMonthPercentage", "usagePeriodPercentage");
            if (t3FourHour is not null)
            {
                var t3Reset = FindDate(properties, "usageFourHourNextResetAt", "usageWindowNextResetAt");
                if (t3Reset is not null)
                {
                    candidates.Add(new UsageWindowCandidate(
                        new UsageWindow(Math.Clamp(t3FourHour.Value, 0, 100), t3Reset.Value, 4 * 60 * 60),
                        WindowKind.Primary));
                }
            }

            if (t3Monthly is not null)
            {
                var t3Reset = FindDate(properties, "currentPeriodEnd");
                if (t3Reset is not null)
                {
                    candidates.Add(new UsageWindowCandidate(
                        new UsageWindow(Math.Clamp(t3Monthly.Value, 0, 100), t3Reset.Value, 30 * 24 * 60 * 60),
                        WindowKind.Secondary));
                }
            }
            if (used is not null || utilization is not null || remaining is not null || (limit is > 0 && amountUsed is not null))
            {
                var percentUsed = used is not null
                    ? NormalizeDirectPercent(used.Value)
                    : NormalizeUtilization(utilization)
                        ?? (remaining is not null
                            ? 100d - NormalizeDirectPercent(remaining.Value)
                            : 100d * amountUsed!.Value / limit!.Value);
                var windowName = FindString(properties, "type", "period", "window", "name", "limit_name");
                var classificationPath = string.IsNullOrWhiteSpace(windowName) ? path : $"{path}.{windowName}";
                if (properties.Any(property => property.Name.Contains("weekly", StringComparison.OrdinalIgnoreCase)))
                {
                    classificationPath += ".weekly";
                }
                var seconds = FindNumber(properties, "window_seconds", "windowSeconds", "period_seconds", "periodSeconds")
                    ?? FindQuotaWindowSeconds(properties)
                    ?? GuessWindowSeconds(classificationPath);
                if (effectiveReset is not null && double.IsFinite(percentUsed))
                {
                    var resolvedSeconds = seconds ?? 0;
                    var kind = ClassifyWindow(classificationPath, resolvedSeconds);
                    candidates.Add(new UsageWindowCandidate(
                        new UsageWindow(
                            Math.Clamp(percentUsed, 0, 100),
                            effectiveReset.Value,
                            (int)Math.Clamp(resolvedSeconds, 0, int.MaxValue)),
                        kind));
                }
            }

            foreach (var property in properties)
            {
                CollectObjects(property.Value, Join(path, property.Name), candidates, fetchedAt, depth + 1, effectiveReset);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                CollectObjects(item, Join(path, index.ToString(CultureInfo.InvariantCulture)), candidates, fetchedAt, depth + 1, inheritedReset);
                index++;
            }
        }
    }

    private static bool TryParseDeepSeekBalance(
        UsageProviderDescriptor descriptor,
        string source,
        JsonElement root,
        DateTimeOffset fetchedAt,
        out UsageSnapshot snapshot)
    {
        snapshot = null!;
        if (!root.TryGetProperty("balance_infos", out var balances)
            || balances.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<(decimal Amount, string Currency)>();
        foreach (var balance in balances.EnumerateArray())
        {
            if (balance.ValueKind != JsonValueKind.Object
                || !balance.TryGetProperty("total_balance", out var amountElement))
            {
                continue;
            }

            var amountText = amountElement.ValueKind == JsonValueKind.Number
                ? amountElement.GetRawText()
                : amountElement.GetString();
            if (!decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                continue;
            }

            var currency = balance.TryGetProperty("currency", out var currencyElement)
                ? currencyElement.GetString()
                : null;
            values.Add((amount, string.IsNullOrWhiteSpace(currency) ? "" : currency.Trim().ToUpperInvariant()));
        }

        if (values.Count == 0)
        {
            return false;
        }

        var singleCurrency = values.Select(value => value.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        var metrics = values.Select(value => new UsageMetric(
            string.IsNullOrWhiteSpace(value.Currency) ? "Total balance" : $"Total balance ({value.Currency})",
            value.Amount.ToString(CultureInfo.InvariantCulture),
            SemanticKey: singleCurrency ? "totalBalance" : "balance",
            NumericValue: value.Amount,
            CurrencyCode: value.Currency)).ToArray();
        snapshot = new UsageSnapshot(
            descriptor.Id,
            descriptor.DisplayName,
            null,
            null,
            null,
            false)
        {
            FetchedAt = fetchedAt,
            Source = source,
            Metrics = metrics,
        };
        return true;
    }

    private static void CollectLeaves(JsonElement element, string path, List<(string Path, JsonElement Value)> leaves, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectLeaves(property.Value, Join(path, property.Name), leaves, depth + 1);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectLeaves(item, Join(path, index.ToString(CultureInfo.InvariantCulture)), leaves, depth + 1);
                    index++;
                }

                break;
            case JsonValueKind.Number:
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
                leaves.Add((path, element));
                break;
        }
    }

    private static bool IsInteresting(string path, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(path) || value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        var lower = path.ToLowerInvariant();
        return InterestingWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
    }

    private static string? FormatValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
            JsonValueKind.True => "true",
            _ => null,
        };

    private static string PrettyName(string path)
    {
        var name = path.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;
        return string.Concat(name.Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()))
            .Replace('_', ' ')
            .Replace('-', ' ');
    }

    private static double? FindNumber(JsonProperty[] properties, params string[] names)
    {
        foreach (var name in names)
        {
            var property = properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                return number;
            }
            if (property.Value.ValueKind == JsonValueKind.String
                && double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static double? FindQuotaWindowSeconds(JsonProperty[] properties)
    {
        var unit = FindNumber(properties, "unit");
        var count = FindNumber(properties, "number");
        if (unit is null || count is null || count <= 0)
        {
            return null;
        }

        var multiplier = unit.Value switch
        {
            1 => 24 * 60 * 60,
            3 => 60 * 60,
            5 => 60,
            6 => 7 * 24 * 60 * 60,
            _ => 0,
        };
        return multiplier > 0 ? count.Value * multiplier : null;
    }

    private static string? FindString(JsonProperty[] properties, params string[] names)
    {
        foreach (var name in names)
        {
            var property = properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static DateTimeOffset? FindDate(JsonProperty[] properties, params string[] names)
    {
        foreach (var name in names)
        {
            var property = properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (property.Value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
            if (property.Value.ValueKind == JsonValueKind.String
                && double.TryParse(property.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var stringNumber))
            {
                return stringNumber > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)stringNumber)
                    : DateTimeOffset.FromUnixTimeSeconds((long)stringNumber);
            }
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
            {
                return number > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)number)
                    : DateTimeOffset.FromUnixTimeSeconds((long)number);
            }
        }

        return null;
    }

    private static string? FindString(JsonElement root, params string[] names)
    {
        foreach (var property in EnumerateProperties(root))
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var nested in EnumerateProperties(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var property in EnumerateProperties(item))
                {
                    yield return property;
                }
            }
        }
    }

    private static double? GuessWindowSeconds(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("week") || lower.Contains("7d") || lower.Contains("weekly")) return 7 * 24 * 60 * 60;
        if (lower.Contains("month")) return 30 * 24 * 60 * 60;
        if (lower.Contains("day") || lower.Contains("24h")) return 24 * 60 * 60;
        if (lower.Contains("hour") || lower.Contains("5h") || lower.Contains("session")) return 5 * 60 * 60;
        return null;
    }

    private static WindowKind ClassifyWindow(string path, double seconds)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("week") || lower.Contains("weekly") || seconds >= 6 * 24 * 60 * 60
            ? WindowKind.Secondary
            : WindowKind.Primary;
    }

    private static string Join(string path, string part)
        => string.IsNullOrWhiteSpace(path) ? part : $"{path}.{part}";

    private static ProviderSpecificWindows FindProviderSpecificWindows(string providerId, JsonElement root)
    {
        if (providerId.Equals("kimi", StringComparison.OrdinalIgnoreCase))
        {
            return FindKimiWindows(root);
        }

        UsageWindow? primary = null;
        UsageWindow? secondary = null;
        var found = false;
        foreach (var properties in EnumerateObjectProperties(root, 0))
        {
            if (providerId.Equals("qwencloud", StringComparison.OrdinalIgnoreCase)
                || providerId.Equals("alibabatokenplan", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateFractionWindow(
                        FindNumber(properties, "per5HourPercentage"),
                        FindDate(properties, "per5HourResetTime"),
                        5 * 60 * 60,
                        out var fiveHour))
                {
                    primary ??= fiveHour;
                    found = true;
                }

                if (TryCreateFractionWindow(
                        FindNumber(properties, "per1WeekPercentage"),
                        FindDate(properties, "per1WeekResetTime"),
                        7 * 24 * 60 * 60,
                        out var weekly))
                {
                    secondary ??= weekly;
                    found = true;
                }
            }

            if (providerId.Equals("stepfun", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateRemainingFractionWindow(
                        FindNumber(properties, "five_hour_usage_left_rate"),
                        FindDate(properties, "five_hour_usage_reset_time"),
                        5 * 60 * 60,
                        out var fiveHour))
                {
                    primary ??= fiveHour;
                    found = true;
                }

                if (TryCreateRemainingFractionWindow(
                        FindNumber(properties, "weekly_usage_left_rate"),
                        FindDate(properties, "weekly_usage_reset_time"),
                        7 * 24 * 60 * 60,
                        out var weekly))
                {
                    secondary ??= weekly;
                    found = true;
                }

                if (primary is null
                    && TryCreateRemainingFractionWindow(
                        FindNumber(properties, "subscription_credit_left_rate", "topup_credit_left_rate"),
                        FindDate(properties, "subscription_credit_reset_time", "next_reset_at"),
                        30 * 24 * 60 * 60,
                        out var credits))
                {
                    primary = credits;
                    found = true;
                }
            }

            if (providerId.Equals("windsurf", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateRemainingWindow(
                        FindNumber(properties, "dailyQuotaRemainingPercent", "daily_remaining_percent"),
                        FindDate(properties, "dailyQuotaResetAtUnix", "daily_reset_at_unix"),
                        24 * 60 * 60,
                        out var daily))
                {
                    primary ??= daily;
                    found = true;
                }

                if (TryCreateRemainingWindow(
                        FindNumber(properties, "weeklyQuotaRemainingPercent", "weekly_remaining_percent"),
                        FindDate(properties, "weeklyQuotaResetAtUnix", "weekly_reset_at_unix"),
                        7 * 24 * 60 * 60,
                        out var weekly))
                {
                    secondary ??= weekly;
                    found = true;
                }
            }

            if (providerId.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
                && TryCreateRemainingFractionWindow(
                    FindNumber(properties, "remainingFraction"),
                    FindDate(properties, "resetTime"),
                    0,
                    out var antigravity))
            {
                if (primary is null || antigravity.UsedPercent > primary.UsedPercent)
                {
                    primary = antigravity;
                }

                found = true;
            }

            if (providerId.Equals("clawrouter", StringComparison.OrdinalIgnoreCase)
                && TryCreateBudgetWindow(properties, out var budget))
            {
                if (primary is null || budget.UsedPercent > primary.UsedPercent)
                {
                    primary = budget;
                }

                found = true;
            }
        }

        return new ProviderSpecificWindows(found, primary, secondary);
    }

    private static ProviderSpecificWindows FindKimiWindows(JsonElement root)
    {
        UsageWindow? primary = null;
        UsageWindow? secondary = null;
        var found = false;

        foreach (var (element, path) in EnumerateObjectElements(root, string.Empty, 0))
        {
            if (element.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object
                && TryCreateKimiWindow(detail, FindKimiWindowSeconds(element, path), out var detailedWindow))
            {
                if (FindKimiWindowSeconds(element, path) >= 6 * 24 * 60 * 60)
                {
                    secondary ??= detailedWindow;
                }
                else
                {
                    primary ??= detailedWindow;
                }

                found = true;
            }

            if (TryCreateKimiWindow(
                    element,
                    path.Contains("limits", StringComparison.OrdinalIgnoreCase) ? 5 * 60 * 60 : 7 * 24 * 60 * 60,
                    out var directWindow))
            {
                if (path.Contains("limits", StringComparison.OrdinalIgnoreCase))
                {
                    primary ??= directWindow;
                }
                else
                {
                    secondary ??= directWindow;
                }

                found = true;
            }
        }

        return new ProviderSpecificWindows(found, primary, secondary);
    }

    private static bool TryCreateKimiWindow(JsonElement detail, int windowSeconds, out UsageWindow window)
    {
        if (detail.ValueKind != JsonValueKind.Object)
        {
            window = null!;
            return false;
        }

        var properties = detail.EnumerateObject().ToArray();
        var limit = FindNumber(properties, "limit", "limitValue", "quota", "total");
        var used = FindNumber(properties, "used", "usage", "consumed");
        var remaining = FindNumber(properties, "remaining", "balance");
        var reset = FindDate(properties, "resetTime", "reset_time", "resetAt", "reset_at");
        if (limit is null || limit <= 0 || reset is null || (used is null && remaining is null))
        {
            window = null!;
            return false;
        }

        var usedPercent = used is not null
            ? used.Value / limit.Value * 100
            : 100 - remaining!.Value / limit.Value * 100;
        window = new UsageWindow(Math.Clamp(usedPercent, 0, 100), reset.Value, windowSeconds);
        return true;
    }

    private static int FindKimiWindowSeconds(JsonElement element, string path)
    {
        if (element.TryGetProperty("window", out var window)
            && window.ValueKind == JsonValueKind.Object
            && TryGetJsonInt(window, "duration", out var duration)
            && duration > 0
            && TryGetJsonString(window, "timeUnit", out var timeUnit))
        {
            var multiplier = timeUnit.ToUpperInvariant() switch
            {
                "TIME_UNIT_MINUTE" => 60,
                "TIME_UNIT_HOUR" => 60 * 60,
                "TIME_UNIT_DAY" => 24 * 60 * 60,
                _ => 0,
            };
            if (multiplier > 0)
            {
                return (int)Math.Clamp((long)duration * multiplier, 0, int.MaxValue);
            }
        }

        return path.Contains("limits", StringComparison.OrdinalIgnoreCase)
            ? 5 * 60 * 60
            : 7 * 24 * 60 * 60;
    }

    private static bool TryGetJsonInt(JsonElement objectElement, string propertyName, out int value)
    {
        if (objectElement.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetJsonString(JsonElement objectElement, string propertyName, out string value)
    {
        if (objectElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<(JsonElement Element, string Path)> EnumerateObjectElements(
        JsonElement element,
        string path,
        int depth)
    {
        if (depth > 8)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return (element, path);
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjectElements(property.Value, Join(path, property.Name), depth + 1))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjectElements(item, Join(path, index.ToString(CultureInfo.InvariantCulture)), depth + 1))
                {
                    yield return nested;
                }

                index++;
            }
        }
    }

    private static bool TryCreateWindow(
        double? directPercent,
        DateTimeOffset? resetAt,
        int windowSeconds,
        out UsageWindow window)
    {
        if (directPercent is not null && resetAt is not null)
        {
            window = new UsageWindow(
                Math.Clamp(NormalizeDirectPercent(directPercent.Value), 0, 100),
                resetAt.Value,
                windowSeconds);
            return true;
        }

        window = null!;
        return false;
    }

    private static bool TryCreateRemainingWindow(
        double? remaining,
        DateTimeOffset? resetAt,
        int windowSeconds,
        out UsageWindow window)
    {
        if (remaining is not null && resetAt is not null)
        {
            var remainingPercent = NormalizeDirectPercent(remaining.Value);
            window = new UsageWindow(
                Math.Clamp(100 - remainingPercent, 0, 100),
                resetAt.Value,
                windowSeconds);
            return true;
        }

        window = null!;
        return false;
    }

    private static bool TryCreateFractionWindow(
        double? fraction,
        DateTimeOffset? resetAt,
        int windowSeconds,
        out UsageWindow window)
    {
        if (fraction is >= 0 and <= 1 && resetAt is not null)
        {
            window = new UsageWindow(fraction.Value * 100, resetAt.Value, windowSeconds);
            return true;
        }

        window = null!;
        return false;
    }

    private static bool TryCreateRemainingFractionWindow(
        double? remainingFraction,
        DateTimeOffset? resetAt,
        int windowSeconds,
        out UsageWindow window)
    {
        if (remainingFraction is >= 0 and <= 1 && resetAt is not null)
        {
            window = new UsageWindow(100 - (remainingFraction.Value * 100), resetAt.Value, windowSeconds);
            return true;
        }

        window = null!;
        return false;
    }

    // Fields explicitly named "percent" are already percent units: 1 means 1%,
    // not a ratio of 1. Ratio-like fields are handled by NormalizeUtilization.
    private static double NormalizeDirectPercent(double value) => value;

    private static bool TryCreateBudgetWindow(JsonProperty[] properties, out UsageWindow window)
    {
        var limit = FindNumber(properties, "limitMicros");
        var spent = FindNumber(properties, "spentMicros");
        var windowKey = FindString(properties, "windowKey");
        if (limit is null || spent is null || limit <= 0 || !TryGetNextMonthReset(windowKey, out var resetAt))
        {
            window = null!;
            return false;
        }

        window = new UsageWindow(Math.Clamp(spent.Value / limit.Value * 100, 0, 100), resetAt, 0);
        return true;
    }

    private static bool TryGetNextMonthReset(string? windowKey, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (string.IsNullOrWhiteSpace(windowKey)
            || !Regex.IsMatch(windowKey, @"^\d{4}-\d{2}$", RegexOptions.CultureInvariant))
        {
            return false;
        }

        var parts = windowKey.Split('-');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        if (month == 12)
        {
            year++;
            month = 1;
        }
        else
        {
            month++;
        }

        resetAt = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        return true;
    }

    private static IEnumerable<JsonProperty[]> EnumerateObjectProperties(JsonElement element, int depth)
    {
        if (depth > 8)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element.EnumerateObject().ToArray();
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateObjectProperties(property.Value, depth + 1))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateObjectProperties(item, depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }

    private static double? NormalizeUtilization(double? utilization)
    {
        if (utilization is null)
        {
            return null;
        }

        return utilization.Value is >= 0 and <= 1
            ? utilization.Value * 100
            : utilization.Value;
    }

    private sealed record ProviderSpecificWindows(bool Found, UsageWindow? Primary, UsageWindow? Secondary);

    private sealed record UsageWindowCandidate(UsageWindow Window, WindowKind Kind);

    private enum WindowKind
    {
        Primary,
        Secondary,
    }
}
