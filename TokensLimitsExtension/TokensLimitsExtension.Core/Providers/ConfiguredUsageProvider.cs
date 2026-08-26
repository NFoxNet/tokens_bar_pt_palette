using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TokensLimitsExtension.Core.Models;

namespace TokensLimitsExtension.Core.Providers;

public sealed class UsageProviderConfigurationException(string message) : InvalidOperationException(message);

public sealed class UsageProviderRequestException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// A provider adapter for the provider-specific endpoint inventory. Authentication
/// and parsing are deliberately shared, while endpoint URLs and response fields
/// remain data-driven so adding a provider does not touch the Dock or pages.
/// </summary>
public sealed class ConfiguredUsageProvider : IUsageProvider, IDisposable
{
    private readonly UsageProviderDescriptor _descriptor;
    private readonly IUsageProviderConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _logger;
    private int _disposed;

    public ConfiguredUsageProvider(
        UsageProviderDescriptor descriptor,
        IUsageProviderConfiguration configuration,
        HttpClient httpClient,
        Action<string>? logger = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? (_ => { });
    }

    public UsageProviderDescriptor Descriptor => _descriptor;

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_descriptor.AuthKind == UsageProviderAuthKind.Local)
        {
            return await GetLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        var endpoints = UsageProviderEndpointCatalog.For(_descriptor.Id);
        if (endpoints.Count == 0)
        {
            throw new UsageProviderConfigurationException(
                $"Для провайдера {_descriptor.DisplayName} не зарегистрирован источник данных.");
        }

        var credential = ResolveCredential();
        var failures = new List<Exception>();
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(credential.ApiKey))
            {
                failures.Add(new UsageProviderConfigurationException(
                    $"Для {Descriptor.DisplayName} не задан API-ключ."));
                continue;
            }

            if (endpoint.RequiresCookie && string.IsNullOrWhiteSpace(credential.CookieHeader))
            {
                failures.Add(new UsageProviderConfigurationException(
                    $"Для {Descriptor.DisplayName} не задан Cookie-заголовок."));
                continue;
            }

            try
            {
                using var request = CreateRequest(endpoint, credential);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add(new UsageProviderRequestException(
                        $"{endpoint.Name}: HTTP {(int)response.StatusCode} ({response.StatusCode})."));
                    continue;
                }

                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
                var snapshot = UsageJsonParser.Parse(
                    _descriptor,
                    endpoint.Name,
                    document.RootElement,
                    DateTimeOffset.UtcNow);
                _logger($"[TokensLimits] Provider {_descriptor.Id}: snapshot fetched from {endpoint.Name}.");
                return snapshot;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UsageProviderRequestException)
            {
                failures.Add(ex);
            }
        }

        throw new UsageProviderRequestException(
            $"Не удалось получить реальные данные {_descriptor.DisplayName}: {DescribeFailures(failures)}",
            failures.LastOrDefault());
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }

    private HttpRequestMessage CreateRequest(UsageProviderEndpoint endpoint, ResolvedCredential credential)
    {
        var url = ResolveUrl(endpoint);
        var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
        }

        if (endpoint.RequiresApiKey && !string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(
                endpoint.ApiKeyHeader ?? "Authorization",
                endpoint.ApiKeyPrefix + credential.ApiKey);
        }

        if (!string.Equals(endpoint.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new StringContent(
                endpoint.RequestBody ?? "{}",
                Encoding.UTF8,
                "application/json");
        }

        if (endpoint.Name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            && endpoint.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "TokensLimitsExtension/0.0.2");
        }

        return request;
    }

    private Uri ResolveUrl(UsageProviderEndpoint endpoint)
    {
        var endpointUrl = endpoint.Url;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} не задан URL источника данных.");
        }

        var replaced = endpointUrl.Replace(
            "{accountId}",
            Uri.EscapeDataString(_configuration.GetValue(Descriptor.Id, "accountId") ?? string.Empty),
            StringComparison.OrdinalIgnoreCase).Replace(
            "{projectId}",
            Uri.EscapeDataString(_configuration.GetValue(Descriptor.Id, "projectId") ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
        var baseUrl = _configuration.GetValue(Descriptor.Id, "baseUrl");
        if (string.IsNullOrWhiteSpace(baseUrl) && endpoint.RequiresBaseUrl)
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} необходимо задать базовый URL API.");
        }

        Uri? configuredBaseUri = null;
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && !Uri.TryCreate(baseUrl, UriKind.Absolute, out configuredBaseUri))
        {
            throw new UsageProviderConfigurationException(
                $"Базовый URL {Descriptor.DisplayName} имеет неверный формат.");
        }

        if (endpoint.UseConfiguredBaseUrl && configuredBaseUri is not null)
        {
            if (Uri.TryCreate(replaced, UriKind.Absolute, out var overriddenPath))
            {
                return new Uri(configuredBaseUri, overriddenPath.PathAndQuery);
            }

            return new Uri(configuredBaseUri, replaced);
        }

        if (Uri.TryCreate(replaced, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (configuredBaseUri is null)
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} не задан базовый URL API.");
        }

        return new Uri(configuredBaseUri, replaced);
    }

    private async Task<UsageSnapshot> GetLocalSnapshotAsync(CancellationToken cancellationToken)
    {
        var endpoints = UsageProviderEndpointCatalog.For(Descriptor.Id);
        var endpoint = endpoints.Count == 0 ? null : endpoints[0];
        if (Descriptor.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase)
            && endpoint?.Url is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UsageProviderRequestException(
                    $"Ollama API вернул HTTP {(int)response.StatusCode}.");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            return UsageJsonParser.Parse(Descriptor, endpoint.Name, document.RootElement, DateTimeOffset.UtcNow);
        }

        var path = _configuration.GetValue(Descriptor.Id, "dataPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UsageProviderConfigurationException(
                $"Для локального провайдера {Descriptor.DisplayName} укажите путь к файлу данных.");
        }

        if (!File.Exists(path))
        {
            throw new UsageProviderConfigurationException($"Файл данных {path} не найден.");
        }

        var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var documentFromFile = JsonDocument.Parse(raw);
            return UsageJsonParser.Parse(Descriptor, "local", documentFromFile.RootElement, DateTimeOffset.UtcNow);
        }
        catch (JsonException)
        {
            return UsageJsonParser.ParseXml(Descriptor, "local", raw, DateTimeOffset.UtcNow);
        }
    }

    private ResolvedCredential ResolveCredential()
    {
        var apiSetting = Descriptor.Settings.FirstOrDefault(setting => setting.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase));
        var apiKey = _configuration.GetValue(Descriptor.Id, "apiKey")
            ?? (apiSetting?.EnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(apiSetting.EnvironmentVariable));
        var oauthToken = _configuration.GetValue(Descriptor.Id, "oauthToken")
            ?? Environment.GetEnvironmentVariable(GetOAuthEnvironmentVariable());
        if (string.IsNullOrWhiteSpace(oauthToken)
            && Descriptor.Settings.Any(setting => setting.Key.Equals("credentialsJson", StringComparison.OrdinalIgnoreCase)))
        {
            oauthToken = ExtractAccessToken(
                _configuration.GetValue(Descriptor.Id, "credentialsJson")
                ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_OAUTH_CREDENTIALS_JSON"));
        }
        var cookie = _configuration.GetValue(Descriptor.Id, "cookieHeader");

        return new ResolvedCredential(
            apiKey ?? oauthToken,
            cookie);
    }

    private string GetOAuthEnvironmentVariable()
        => Descriptor.Id switch
        {
            "gemini" or "vertexai" => "GOOGLE_OAUTH_ACCESS_TOKEN",
            "copilot" => "GITHUB_COPILOT_TOKEN",
            "antigravity" => "ANTIGRAVITY_OAUTH_ACCESS_TOKEN",
            "claude" => "CLAUDE_OAUTH_ACCESS_TOKEN",
            _ => string.Empty,
        };

    private static string? ExtractAccessToken(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(credentialsJson);
            foreach (var name in new[] { "access_token", "accessToken", "token" })
            {
                if (document.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string DescribeFailures(List<Exception> failures)
        => failures.Count == 0
            ? "источник не отвечает"
            : string.Join("; ", failures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal));

    private sealed record ResolvedCredential(string? ApiKey, string? CookieHeader);
}

internal static class UsageJsonParser
{
    private static readonly string[] InterestingWords =
    [
        "usage", "used", "limit", "remaining", "quota", "credit", "balance", "token", "cost",
        "spend", "request", "character", "point", "plan", "reset", "refill", "budget", "amount",
    ];

    public static UsageSnapshot Parse(
        UsageProviderDescriptor descriptor,
        string source,
        JsonElement root,
        DateTimeOffset fetchedAt)
    {
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

        var windows = FindWindows(root, fetchedAt);
        var plan = FindString(root, "plan", "planName", "tier", "subscription", "product");
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

    private static (UsageWindow? Primary, UsageWindow? Secondary) FindWindows(JsonElement root, DateTimeOffset fetchedAt)
    {
        var candidates = new List<UsageWindowCandidate>();
        CollectObjects(root, string.Empty, candidates, fetchedAt, 0);
        return (
            candidates.FirstOrDefault(candidate => candidate.Kind == WindowKind.Primary)?.Window
                ?? candidates.FirstOrDefault()?.Window,
            candidates.FirstOrDefault(candidate => candidate.Kind == WindowKind.Secondary)?.Window
                ?? (candidates.Count > 1 ? candidates[1].Window : null));
    }

    private static void CollectObjects(
        JsonElement element,
        string path,
        List<UsageWindowCandidate> candidates,
        DateTimeOffset fetchedAt,
        int depth)
    {
        if (depth > 8)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            var used = FindNumber(properties, "used_percent", "usedPercent", "usage_percent", "usagePercent", "percentage_used");
            var utilization = FindNumber(properties, "utilization", "utilization_percent", "usage_percentage", "usagePercentage");
            var remaining = FindNumber(properties, "remaining_percent", "remainingPercent", "percentage_remaining");
            var limit = FindNumber(properties, "limit", "quota", "max", "maximum");
            var amountUsed = FindNumber(properties, "used", "usage", "consumed", "current");
            var reset = FindDate(properties, "reset_at", "resetAt", "reset", "next_reset", "nextReset", "refill_at", "refillAt");
            if (used is not null || utilization is not null || remaining is not null || (limit is not null && amountUsed is not null))
            {
                var percentUsed = used ?? NormalizeUtilization(utilization)
                    ?? (remaining is not null ? 100d - remaining.Value : 100d * amountUsed!.Value / limit!.Value);
                var seconds = FindNumber(properties, "window_seconds", "windowSeconds", "period_seconds", "periodSeconds")
                    ?? GuessWindowSeconds(path);
                if (reset is not null && seconds is not null)
                {
                    var kind = ClassifyWindow(path, seconds.Value);
                    candidates.Add(new UsageWindowCandidate(
                        new UsageWindow(Math.Clamp(percentUsed, 0, 100), reset.Value, (int)Math.Clamp(seconds.Value, 1, int.MaxValue)),
                        kind));
                }
            }

            foreach (var property in properties)
            {
                CollectObjects(property.Value, Join(path, property.Name), candidates, fetchedAt, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                CollectObjects(item, Join(path, index.ToString(CultureInfo.InvariantCulture)), candidates, fetchedAt, depth + 1);
                index++;
            }
        }
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

    private sealed record UsageWindowCandidate(UsageWindow Window, WindowKind Kind);

    private enum WindowKind
    {
        Primary,
        Secondary,
    }
}
