using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        if (_descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return await GetOpenAiSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("deepgram", StringComparison.OrdinalIgnoreCase))
        {
            return await GetDeepgramSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("qwencloud", StringComparison.OrdinalIgnoreCase)
            || _descriptor.Id.Equals("alibabatokenplan", StringComparison.OrdinalIgnoreCase))
        {
            return await GetAlibabaGatewaySnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("t3chat", StringComparison.OrdinalIgnoreCase))
        {
            return await GetT3ChatSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        var endpoints = UsageProviderEndpointCatalog.For(_descriptor.Id);
        if (endpoints.Count == 0)
        {
            throw new UsageProviderConfigurationException(
                $"Для провайдера {_descriptor.DisplayName} не зарегистрирован источник данных.");
        }

        var credential = ResolveCredential();
        var failures = new List<Exception>();
        var snapshots = new List<UsageSnapshot>();
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

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = UsageJsonParser.ParseText(
                    _descriptor,
                    endpoint.Name,
                    body,
                    DateTimeOffset.UtcNow);
                snapshots.Add(snapshot);
                _logger($"[TokensLimits] Provider {_descriptor.Id}: snapshot fetched from {endpoint.Name}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or XmlException or InvalidOperationException or UsageProviderRequestException or UsageProviderConfigurationException)
            {
                failures.Add(ex);
            }
        }

        if (snapshots.Count > 0)
        {
            return UsageJsonParser.Merge(_descriptor, snapshots);
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

        if (Descriptor.Id.Equals("manus", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            var session = Regex.Match(
                credential.CookieHeader,
                @"(?:^|;\s*)session_id=([^;]+)",
                RegexOptions.IgnoreCase);
            if (session.Success)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {session.Groups[1].Value}");
            }

            request.Headers.TryAddWithoutValidation("Origin", "https://manus.im");
            request.Headers.TryAddWithoutValidation("Referer", "https://manus.im/");
            request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        }

        if (endpoint.RequiresApiKey && !string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(
                endpoint.ApiKeyHeader ?? "Authorization",
                endpoint.ApiKeyPrefix + credential.ApiKey);
        }

        if (endpoint.Headers is not null)
        {
            foreach (var header in endpoint.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (Descriptor.Id.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            var referer = _configuration.GetValue(Descriptor.Id, "httpReferer");
            var title = _configuration.GetValue(Descriptor.Id, "clientTitle");
            if (!string.IsNullOrWhiteSpace(referer))
            {
                request.Headers.TryAddWithoutValidation("HTTP-Referer", referer);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                request.Headers.TryAddWithoutValidation("X-Title", title);
            }
        }

        if (Descriptor.Id.Equals("zai", StringComparison.OrdinalIgnoreCase)
            && _configuration.GetValue(Descriptor.Id, "scope")?.Equals("team", StringComparison.OrdinalIgnoreCase) == true)
        {
            var organization = _configuration.GetValue(Descriptor.Id, "accountId");
            var project = _configuration.GetValue(Descriptor.Id, "projectId");
            if (!string.IsNullOrWhiteSpace(organization))
            {
                request.Headers.TryAddWithoutValidation("Bigmodel-Organization", organization);
            }

            if (!string.IsNullOrWhiteSpace(project))
            {
                request.Headers.TryAddWithoutValidation("Bigmodel-Project", project);
            }
        }

        if (!string.Equals(endpoint.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(endpoint.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new StringContent(
                ResolveRequestBody(endpoint),
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

    private static string ResolveRequestBody(UsageProviderEndpoint endpoint)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddDays(-30);
        return (endpoint.RequestBody ?? "{}")
            .Replace("{startTime}", FormatAnalyticsTimestamp(start), StringComparison.Ordinal)
            .Replace("{endTime}", FormatAnalyticsTimestamp(now), StringComparison.Ordinal);
    }

    private static string FormatAnalyticsTimestamp(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private Uri ResolveUrl(UsageProviderEndpoint endpoint)
    {
        var endpointUrl = ResolveProviderEndpointUrl(endpoint);
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} не задан URL источника данных.");
        }

        var accountId = _configuration.GetValue(Descriptor.Id, "accountId");
        var projectId = _configuration.GetValue(Descriptor.Id, "projectId");
        var deploymentName = _configuration.GetValue(Descriptor.Id, "deploymentName");
        var apiVersion = _configuration.GetValue(Descriptor.Id, "apiVersion");
        if (endpointUrl.Contains("{accountId}", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(accountId))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} укажите идентификатор организации или аккаунта.");
        }

        if (endpointUrl.Contains("{projectId}", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(projectId))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} укажите идентификатор проекта.");
        }

        if (endpointUrl.Contains("{deploymentName}", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(deploymentName))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} укажите имя deployment.");
        }

        if (endpointUrl.Contains("{apiVersion}", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(apiVersion))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} укажите версию API.");
        }

        var replaced = endpointUrl.Replace(
            "{accountId}",
            Uri.EscapeDataString(accountId ?? string.Empty),
            StringComparison.OrdinalIgnoreCase).Replace(
            "{projectId}",
            Uri.EscapeDataString(projectId ?? string.Empty),
            StringComparison.OrdinalIgnoreCase).Replace(
            "{deploymentName}",
            Uri.EscapeDataString(deploymentName ?? string.Empty),
            StringComparison.OrdinalIgnoreCase).Replace(
            "{apiVersion}",
            Uri.EscapeDataString(apiVersion ?? string.Empty),
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
                return AddProviderQuery(new Uri(configuredBaseUri, overriddenPath.PathAndQuery), endpoint);
            }

            return AddProviderQuery(new Uri(configuredBaseUri, replaced), endpoint);
        }

        if (Uri.TryCreate(replaced, UriKind.Absolute, out var absolute))
        {
            return AddProviderQuery(absolute, endpoint);
        }

        if (configuredBaseUri is null)
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} не задан базовый URL API.");
        }

        return AddProviderQuery(new Uri(configuredBaseUri, replaced), endpoint);
    }

    private string? ResolveProviderEndpointUrl(UsageProviderEndpoint endpoint)
    {
        var endpointUrl = endpoint.Url;
        if (!Descriptor.Id.Equals("zai", StringComparison.OrdinalIgnoreCase))
        {
            return endpointUrl;
        }

        var settingKey = endpoint.Name.ToLowerInvariant() switch
        {
            "quota" => "quotaEndpoint",
            "model-usage" => "modelUsageEndpoint",
            "balance-cn" => "balanceEndpoint",
            _ => null,
        };
        var overrideUrl = settingKey is null ? null : _configuration.GetValue(Descriptor.Id, settingKey);
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl;
        }

        if (_configuration.GetValue(Descriptor.Id, "region")?.Equals("bigmodel-cn", StringComparison.OrdinalIgnoreCase) == true
            && endpointUrl is not null)
        {
            return endpointUrl.Replace("api.z.ai", "open.bigmodel.cn", StringComparison.OrdinalIgnoreCase);
        }

        return endpointUrl;
    }

    private Uri AddProviderQuery(Uri uri, UsageProviderEndpoint endpoint)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            query.Add(uri.Query.TrimStart('?'));
        }

        var now = DateTimeOffset.UtcNow;
        if (Descriptor.Id.Equals("claude", StringComparison.OrdinalIgnoreCase)
            && endpoint.Name.StartsWith("organization-", StringComparison.OrdinalIgnoreCase))
        {
            query.Add($"starting_at={Uri.EscapeDataString(now.AddDays(-30).ToString("O", CultureInfo.InvariantCulture))}");
            query.Add($"ending_at={Uri.EscapeDataString(now.ToString("O", CultureInfo.InvariantCulture))}");
            query.Add("bucket_width=1d");
            query.Add($"group_by%5B%5D={(endpoint.Name.EndsWith("cost", StringComparison.OrdinalIgnoreCase) ? "description" : "model")}");
        }
        else if (Descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
            && (endpoint.Name.Equals("costs", StringComparison.OrdinalIgnoreCase)
                || endpoint.Name.Equals("usage", StringComparison.OrdinalIgnoreCase)))
        {
            query.Add($"start_time={now.AddDays(-30).ToUnixTimeSeconds()}");
            query.Add($"end_time={now.ToUnixTimeSeconds()}");
            query.Add("bucket_width=1d");
            query.Add($"group_by={(endpoint.Name.Equals("costs", StringComparison.OrdinalIgnoreCase) ? "line_item" : "model")}");
            var projectId = _configuration.GetValue(Descriptor.Id, "projectId");
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                query.Add($"project_ids%5B%5D={Uri.EscapeDataString(projectId)}");
            }
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", query),
        };
        return builder.Uri;
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
            return UsageJsonParser.ParseOllama(Descriptor, document.RootElement, DateTimeOffset.UtcNow);
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

    private async Task<UsageSnapshot> GetOpenAiSnapshotAsync(CancellationToken cancellationToken)
    {
        var apiKey = _configuration.GetValue(Descriptor.Id, "adminApiKey")
            ?? Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY")
            ?? _configuration.GetValue(Descriptor.Id, "apiKey")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new UsageProviderConfigurationException(
                "Для OpenAI нужен Admin API-ключ (OPENAI_ADMIN_KEY или поле Admin API-ключ). ");
        }

        var baseUrl = _configuration.GetValue(Descriptor.Id, "baseUrl") ?? "https://api.openai.com";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new UsageProviderConfigurationException("Базовый URL OpenAI имеет неверный формат.");
        }

        var historyDays = 30;
        var configuredHistoryDays = _configuration.GetValue(Descriptor.Id, "historyDays");
        if (!string.IsNullOrWhiteSpace(configuredHistoryDays)
            && (!int.TryParse(configuredHistoryDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out historyDays)
                || historyDays is < 1 or > 365))
        {
            throw new UsageProviderConfigurationException("История OpenAI должна быть целым числом от 1 до 365 дней.");
        }

        var projectId = _configuration.GetValue(Descriptor.Id, "projectId");
        var totals = new OpenAiUsageTotals();
        var now = DateTimeOffset.UtcNow;
        var start = now.UtcDateTime.Date.AddDays(-(historyDays - 1));
        var remainingDays = historyDays;
        while (remainingDays > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkDays = Math.Min(31, remainingDays);
            var rangeStart = new DateTimeOffset(start, TimeSpan.Zero);
            var rangeEnd = rangeStart.AddDays(chunkDays);
            await CollectOpenAiPagesAsync(
                baseUri,
                "/v1/organization/costs",
                "line_item",
                rangeStart.ToUnixTimeSeconds(),
                rangeEnd.ToUnixTimeSeconds(),
                projectId,
                apiKey,
                totals,
                costs: true,
                cancellationToken).ConfigureAwait(false);
            await CollectOpenAiPagesAsync(
                baseUri,
                "/v1/organization/usage/completions",
                "model",
                rangeStart.ToUnixTimeSeconds(),
                rangeEnd.ToUnixTimeSeconds(),
                projectId,
                apiKey,
                totals,
                costs: false,
                cancellationToken).ConfigureAwait(false);
            start = start.AddDays(chunkDays);
            remainingDays -= chunkDays;
        }

        var metrics = new List<UsageMetric>
        {
            new("Spend", FormatNumber(totals.Cost), "USD", Used: totals.Cost),
            new("Requests", FormatNumber(totals.Requests), "requests", Used: totals.Requests),
            new("Tokens", FormatNumber(totals.Tokens), "tokens", Used: totals.Tokens),
            new("Input tokens", FormatNumber(totals.InputTokens), "tokens", Used: totals.InputTokens),
            new("Cached input tokens", FormatNumber(totals.CachedInputTokens), "tokens", Used: totals.CachedInputTokens),
            new("Output tokens", FormatNumber(totals.OutputTokens), "tokens", Used: totals.OutputTokens),
            new("History", historyDays.ToString(CultureInfo.InvariantCulture), "days"),
        };
        foreach (var model in totals.Models.OrderByDescending(pair => pair.Value.Tokens).Take(24))
        {
            metrics.Add(new UsageMetric(
                $"Model {model.Key}",
                FormatNumber(model.Value.Tokens),
                "tokens",
                Used: model.Value.Tokens));
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: fetched OpenAI costs and completion usage for {historyDays} days.");
        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            null,
            null,
            "Admin API",
            false)
        {
            FetchedAt = now,
            Source = "organization/costs + organization/usage/completions",
            Metrics = metrics,
        };
    }

    private async Task CollectOpenAiPagesAsync(
        Uri baseUri,
        string path,
        string groupBy,
        long startTime,
        long endTime,
        string? projectId,
        string apiKey,
        OpenAiUsageTotals totals,
        bool costs,
        CancellationToken cancellationToken)
    {
        string? page = null;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var requestUri = BuildOpenAiUri(
                baseUri,
                path,
                startTime,
                endTime,
                groupBy,
                projectId,
                page);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UsageProviderRequestException(
                    $"OpenAI {path}: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var hasMore)
                || hasMore.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new UsageProviderRequestException($"OpenAI {path}: ответ не похож на страницу Usage API.");
            }

            foreach (var bucket in data.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object
                    || !bucket.TryGetProperty("results", out var results)
                    || results.ValueKind != JsonValueKind.Array)
                {
                    throw new UsageProviderRequestException($"OpenAI {path}: бакет не содержит массив results.");
                }

                foreach (var result in results.EnumerateArray())
                {
                    if (result.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (costs)
                    {
                        var amount = result.TryGetProperty("amount", out var amountObject)
                            && amountObject.ValueKind == JsonValueKind.Object
                            && TryGetJsonDouble(amountObject, "value", out var cost)
                            ? cost
                            : 0;
                        totals.Cost += amount;
                    }
                    else
                    {
                        var input = TryGetJsonDouble(result, "input_tokens", out var inputTokens) ? inputTokens : 0;
                        var cached = TryGetJsonDouble(result, "input_cached_tokens", out var cachedTokens) ? cachedTokens : 0;
                        var audioInput = TryGetJsonDouble(result, "input_audio_tokens", out var inputAudioTokens) ? inputAudioTokens : 0;
                        var output = TryGetJsonDouble(result, "output_tokens", out var outputTokens) ? outputTokens : 0;
                        var audioOutput = TryGetJsonDouble(result, "output_audio_tokens", out var outputAudioTokens) ? outputAudioTokens : 0;
                        var requests = TryGetJsonDouble(result, "num_model_requests", out var modelRequests) ? modelRequests : 0;
                        var totalTokens = input + audioInput + output + audioOutput;
                        totals.InputTokens += input + audioInput;
                        totals.CachedInputTokens += cached;
                        totals.OutputTokens += output + audioOutput;
                        totals.Tokens += totalTokens;
                        totals.Requests += requests;
                        var model = result.TryGetProperty("model", out var modelValue)
                            ? modelValue.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(model))
                        {
                            model = "Responses and Chat Completions";
                        }

                        if (!totals.Models.TryGetValue(model, out var modelTotal))
                        {
                            modelTotal = new OpenAiModelTotals();
                            totals.Models[model] = modelTotal;
                        }

                        modelTotal.Tokens += totalTokens;
                        modelTotal.Requests += requests;
                    }
                }
            }

            if (!hasMore.GetBoolean())
            {
                return;
            }

            if (!root.TryGetProperty("next_page", out var nextPageValue)
                || nextPageValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nextPageValue.GetString()))
            {
                throw new UsageProviderRequestException($"OpenAI {path}: отсутствует курсор пагинации.");
            }

            page = nextPageValue.GetString()!.Trim();
            if (!seenPages.Add(page))
            {
                throw new UsageProviderRequestException($"OpenAI {path}: курсор пагинации повторился.");
            }
        }

        throw new UsageProviderRequestException($"OpenAI {path}: пагинация превысила 100 страниц.");
    }

    private static Uri BuildOpenAiUri(
        Uri baseUri,
        string path,
        long startTime,
        long endTime,
        string groupBy,
        string? projectId,
        string? page)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = path,
        };
        var query = new List<string>
        {
            $"start_time={startTime}",
            $"end_time={endTime}",
            "bucket_width=1d",
            "limit=31",
            $"group_by={Uri.EscapeDataString(groupBy)}",
        };
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            query.Add($"project_ids={Uri.EscapeDataString(projectId)}");
        }

        if (!string.IsNullOrWhiteSpace(page))
        {
            query.Add($"page={Uri.EscapeDataString(page)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static bool TryGetJsonDouble(JsonElement objectElement, string propertyName, out double value)
    {
        if (objectElement.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private sealed class OpenAiUsageTotals
    {
        public double Cost { get; set; }
        public double Requests { get; set; }
        public double Tokens { get; set; }
        public double InputTokens { get; set; }
        public double CachedInputTokens { get; set; }
        public double OutputTokens { get; set; }
        public Dictionary<string, OpenAiModelTotals> Models { get; } = new(StringComparer.Ordinal);
    }

    private sealed class OpenAiModelTotals
    {
        public double Requests { get; set; }
        public double Tokens { get; set; }
    }

    private async Task<UsageSnapshot> GetDeepgramSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            throw new UsageProviderConfigurationException("Для Deepgram не задан API-ключ.");
        }

        var baseValue = _configuration.GetValue(Descriptor.Id, "baseUrl") ?? "https://api.deepgram.com/v1";
        if (!Uri.TryCreate(baseValue, UriKind.Absolute, out var baseUri))
        {
            throw new UsageProviderConfigurationException("Базовый URL Deepgram имеет неверный формат.");
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (!basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUri = new Uri(baseUri, basePath + "/v1/");
        }

        var projectId = _configuration.GetValue(Descriptor.Id, "projectId");
        (string Id, string Name)[] projects;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            projects = [(projectId, projectId)];
        }
        else
        {
            var projectsRoot = await GetJsonRootAsync(
                new Uri(baseUri, "projects"),
                credential.ApiKey,
                "Token ",
                cancellationToken).ConfigureAwait(false);
            if (!projectsRoot.TryGetProperty("projects", out var rawProjects)
                || rawProjects.ValueKind != JsonValueKind.Array)
            {
                throw new UsageProviderRequestException("Ответ Deepgram не содержит список проектов.");
            }

            projects = rawProjects.EnumerateArray()
                .Where(project => project.ValueKind == JsonValueKind.Object)
                .Select(project =>
                {
                    var id = project.TryGetProperty("project_id", out var idValue)
                        ? idValue.GetString()
                        : null;
                    var name = project.TryGetProperty("name", out var nameValue)
                        ? nameValue.GetString()
                        : null;
                    return (Id: id ?? string.Empty, Name: string.IsNullOrWhiteSpace(name) ? id ?? string.Empty : name);
                })
                .Where(project => !string.IsNullOrWhiteSpace(project.Id))
                .ToArray();
        }

        if (projects.Length == 0)
        {
            throw new UsageProviderRequestException("Deepgram не вернул ни одного проекта.");
        }

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var periodStart = string.Empty;
        var periodEnd = string.Empty;
        foreach (var project in projects)
        {
            var usageRoot = await GetJsonRootAsync(
                new Uri(baseUri, $"projects/{Uri.EscapeDataString(project.Id)}/usage/breakdown"),
                credential.ApiKey,
                "Token ",
                cancellationToken).ConfigureAwait(false);
            if (usageRoot.TryGetProperty("start", out var startValue))
            {
                periodStart = startValue.GetString() ?? periodStart;
            }

            if (usageRoot.TryGetProperty("end", out var endValue))
            {
                periodEnd = endValue.GetString() ?? periodEnd;
            }

            if (!usageRoot.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw new UsageProviderRequestException("Ответ Deepgram не содержит usage results.");
            }

            foreach (var row in results.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var field in new[] { "hours", "total_hours", "agent_hours", "tokens_in", "tokens_out", "tts_characters", "requests" })
                {
                    if (row.TryGetProperty(field, out var value)
                        && TryGetNumber(value, out var number))
                    {
                        totals[field] = totals.GetValueOrDefault(field) + number;
                    }
                }
            }
        }

        var metrics = new List<UsageMetric>
        {
            new("Requests", FormatNumber(totals.GetValueOrDefault("requests")), "requests"),
        };
        if (totals.GetValueOrDefault("hours") != 0 || totals.GetValueOrDefault("total_hours") != 0)
        {
            metrics.Add(new UsageMetric("Audio", FormatNumber(totals.GetValueOrDefault("hours")), "hours"));
            metrics.Add(new UsageMetric("Billable audio", FormatNumber(totals.GetValueOrDefault("total_hours")), "hours"));
        }

        if (totals.GetValueOrDefault("agent_hours") != 0)
        {
            metrics.Add(new UsageMetric("Agent hours", FormatNumber(totals.GetValueOrDefault("agent_hours")), "hours"));
        }

        if (totals.GetValueOrDefault("tokens_in") != 0 || totals.GetValueOrDefault("tokens_out") != 0)
        {
            metrics.Add(new UsageMetric(
                "Tokens",
                FormatNumber(totals.GetValueOrDefault("tokens_in") + totals.GetValueOrDefault("tokens_out")),
                "tokens"));
        }

        if (totals.GetValueOrDefault("tts_characters") != 0)
        {
            metrics.Add(new UsageMetric("TTS characters", FormatNumber(totals.GetValueOrDefault("tts_characters")), "characters"));
        }

        if (!string.IsNullOrWhiteSpace(periodStart) || !string.IsNullOrWhiteSpace(periodEnd))
        {
            metrics.Add(new UsageMetric("Period", $"{periodStart} — {periodEnd}"));
        }

        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            null,
            null,
            null,
            false)
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Source = "projects/*/usage/breakdown",
            Metrics = metrics,
        };
    }

    private async Task<UsageSnapshot> GetAlibabaGatewaySnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} укажите Cookie-заголовок авторизованной сессии.");
        }

        var isQwen = Descriptor.Id.Equals("qwencloud", StringComparison.OrdinalIgnoreCase);
        var dashboardUrl = isQwen
            ? new Uri("https://home.qwencloud.com/billing/subscription/token-plan-individual")
            : new Uri("https://modelstudio.console.alibabacloud.com/ap-southeast-1/?tab=plan#/efm/subscription/token-plan/personal");
        var apiUrl = isQwen
            ? new Uri("https://cs-data.qwencloud.com/data/api.json")
            : new Uri("https://bailian-singapore-cs.alibabacloud.com/data/api.json");

        var secToken = ExtractSecToken(credential.CookieHeader);
        if (string.IsNullOrWhiteSpace(secToken))
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, dashboardUrl);
            pageRequest.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
            pageRequest.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            using var pageResponse = await _httpClient
                .SendAsync(pageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!pageResponse.IsSuccessStatusCode)
            {
                throw new UsageProviderRequestException(
                    $"Не удалось открыть консоль {Descriptor.DisplayName}: HTTP {(int)pageResponse.StatusCode}.");
            }

            var page = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            secToken = ExtractSecToken(page);
        }

        if (string.IsNullOrWhiteSpace(secToken))
        {
            throw new UsageProviderConfigurationException(
                $"В Cookie/консоли {Descriptor.DisplayName} не найден sec_token. Обновите Cookie после входа в консоль.");
        }

        var action = isQwen ? "IntlBroadScopeAspnGateway" : "IntlBroadScopeAspnGateway";
        var apiName = "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage";
        var cornerstone = new Dictionary<string, object?>
        {
            ["feTraceId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
            ["feURL"] = dashboardUrl.AbsoluteUri,
            ["protocol"] = "V2",
            ["console"] = "ONE_CONSOLE",
            ["productCode"] = "p_efm",
            ["domain"] = dashboardUrl.Host,
            ["consoleSite"] = isQwen ? "QWENCLOUD" : "MODELSTUDIO_ALBABACLOUD",
            ["userNickName"] = "",
            ["userPrincipalName"] = "",
            ["xsp_lang"] = "en-US",
        };
        var parameters = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Api"] = apiName,
            ["V"] = "1.0",
            ["Data"] = new Dictionary<string, object?> { ["cornerstoneParam"] = cornerstone },
        });
        var form = new Dictionary<string, string>
        {
            ["product"] = "sfm_bailian",
            ["action"] = action,
            ["sec_token"] = secToken,
            ["region"] = "ap-southeast-1",
            ["language"] = "en-US",
            ["params"] = parameters,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl);
        request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Origin", dashboardUrl.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", dashboardUrl.AbsoluteUri);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Content = new StringContent(
            string.Join("&", form.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UsageProviderRequestException(
                $"{Descriptor.DisplayName} gateway: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = UsageJsonParser.Parse(Descriptor, "token-plan/usage", document.RootElement, DateTimeOffset.UtcNow);
        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from token-plan gateway.");
        return snapshot;
    }

    private async Task<UsageSnapshot> GetT3ChatSnapshotAsync(CancellationToken cancellationToken)
    {
        var endpoint = UsageProviderEndpointCatalog.For(Descriptor.Id).Single();
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            throw new UsageProviderConfigurationException("Для T3 Chat укажите Cookie-заголовок авторизованной сессии.");
        }

        using var request = CreateRequest(endpoint, credential);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UsageProviderRequestException(
                $"T3 Chat: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                try
                {
                    return UsageJsonParser.Parse(Descriptor, "customer-jsonl", document.RootElement, DateTimeOffset.UtcNow);
                }
                catch (UsageProviderRequestException)
                {
                    // The tRPC stream contains several envelopes. Continue until the
                    // customer object with usageFourHourPercentage is encountered.
                }
            }
            catch (JsonException)
            {
                // JSONL may contain an empty/diagnostic line; ignore that line only.
            }
        }

        throw new UsageProviderRequestException("Ответ T3 Chat не содержит данных customer usage.");
    }

    private static string? ExtractSecToken(string value)
    {
        var cookieMatch = Regex.Match(value, @"(?:^|;\s*)sec_token=([^;]+)", RegexOptions.IgnoreCase);
        if (cookieMatch.Success)
        {
            return cookieMatch.Groups[1].Value;
        }

        var pageMatch = Regex.Match(
            value,
            @"(?:secToken|sec_token)\s*[:=]\s*[""']([^""']+)",
            RegexOptions.IgnoreCase);
        return pageMatch.Success ? pageMatch.Groups[1].Value : null;
    }

    private async Task<JsonElement> GetJsonRootAsync(
        Uri uri,
        string credential,
        string prefix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Authorization", prefix + credential);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UsageProviderRequestException(
                $"Deepgram: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static bool TryGetNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static string FormatNumber(double value)
        => value.ToString(value == Math.Truncate(value) ? "0" : "0.##", CultureInfo.InvariantCulture);

    private ResolvedCredential ResolveCredential()
    {
        var apiSetting = Descriptor.Settings.FirstOrDefault(setting => setting.Key.Equals("apiKey", StringComparison.OrdinalIgnoreCase));
        var apiKey = _configuration.GetValue(Descriptor.Id, "apiKey")
            ?? (apiSetting?.EnvironmentVariable is null ? null : Environment.GetEnvironmentVariable(apiSetting.EnvironmentVariable));
        if (Descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _configuration.GetValue(Descriptor.Id, "adminApiKey")
                ?? Environment.GetEnvironmentVariable("OPENAI_ADMIN_KEY");
        }
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
        "total", "model",
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

        if (descriptor.Id.Equals("azureopenai", StringComparison.OrdinalIgnoreCase))
        {
            var model = FindString(root, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                metrics.Add(new UsageMetric("Model", model));
            }
        }

        var windows = FindWindows(descriptor, root, fetchedAt);
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
            var remaining = FindNumber(properties, "remaining_percent", "remainingPercent", "percentage_remaining", "percentRemaining");
            var limit = FindNumber(properties, "limit", "quota", "max", "maximum", "total", "capacity", "allowance", "grant_amount", "total_granted");
            var amountUsed = FindNumber(properties, "used", "usage", "consumed", "current", "used_amount", "total_used", "currentValue");
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
                "dailyQuotaResetAtUnix",
                "weeklyQuotaResetAtUnix",
                "quotaResetDate");
            var effectiveReset = reset ?? inheritedReset;
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
            if (used is not null || utilization is not null || remaining is not null || (limit is not null && amountUsed is not null))
            {
                var percentUsed = used is not null
                    ? NormalizeDirectPercent(used.Value)
                    : NormalizeUtilization(utilization)
                        ?? (remaining is not null
                            ? 100d - NormalizeDirectPercent(remaining.Value)
                            : 100d * amountUsed!.Value / limit!.Value);
                var windowName = FindString(properties, "type", "period", "window", "name", "limit_name");
                var classificationPath = string.IsNullOrWhiteSpace(windowName) ? path : $"{path}.{windowName}";
                var seconds = FindNumber(properties, "window_seconds", "windowSeconds", "period_seconds", "periodSeconds")
                    ?? FindQuotaWindowSeconds(properties)
                    ?? GuessWindowSeconds(classificationPath);
                if (effectiveReset is not null)
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
        UsageWindow? primary = null;
        UsageWindow? secondary = null;
        var found = false;
        foreach (var properties in EnumerateObjectProperties(root, 0))
        {
            if (providerId.Equals("qwencloud", StringComparison.OrdinalIgnoreCase)
                || providerId.Equals("alibabatokenplan", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCreateWindow(
                        FindNumber(properties, "per5HourPercentage"),
                        FindDate(properties, "per5HourResetTime"),
                        5 * 60 * 60,
                        out var fiveHour))
                {
                    primary ??= fiveHour;
                    found = true;
                }

                if (TryCreateWindow(
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
                if (TryCreateRemainingWindow(
                        FindNumber(properties, "five_hour_usage_left_rate"),
                        FindDate(properties, "five_hour_usage_reset_time"),
                        5 * 60 * 60,
                        out var fiveHour))
                {
                    primary ??= fiveHour;
                    found = true;
                }

                if (TryCreateRemainingWindow(
                        FindNumber(properties, "weekly_usage_left_rate"),
                        FindDate(properties, "weekly_usage_reset_time"),
                        7 * 24 * 60 * 60,
                        out var weekly))
                {
                    secondary ??= weekly;
                    found = true;
                }

                if (primary is null
                    && TryCreateRemainingWindow(
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
                && TryCreateRemainingWindow(
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

    private static double NormalizeDirectPercent(double value)
        => value is >= 0 and <= 1 ? value * 100 : value;

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
