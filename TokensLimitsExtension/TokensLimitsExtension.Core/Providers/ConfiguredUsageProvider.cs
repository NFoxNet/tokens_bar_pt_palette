using System.Buffers;
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

/// <summary>
/// A provider adapter for the provider-specific endpoint inventory. Authentication
/// and parsing are deliberately shared, while endpoint URLs and response fields
/// remain data-driven so adding a provider does not touch the Dock or pages.
/// </summary>
public sealed class ConfiguredUsageProvider : IUsageProvider, IDisposable
{
    private const string ProductUserAgent = "TokensLimitsExtension";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumCancellableTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private const int DefaultMaxResponseBodyBytes = 1_048_576;
    private readonly UsageProviderDescriptor _descriptor;
    private readonly IUsageProviderConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly Action<string> _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxResponseBodyBytes;
    private readonly IUsageProviderProcessRunner _processRunner;
    private int _disposed;

    public ConfiguredUsageProvider(
        UsageProviderDescriptor descriptor,
        IUsageProviderConfiguration configuration,
        HttpClient httpClient,
        Action<string>? logger = null)
        : this(descriptor, configuration, httpClient, logger, null, DefaultMaxResponseBodyBytes, null)
    {
    }

    /// <summary>
    /// Creates a catalog-backed provider with bounds for its generic HTTP endpoint path.
    /// Provider-specific adapters are migrated to the same bounded reader separately.
    /// </summary>
    public ConfiguredUsageProvider(
        UsageProviderDescriptor descriptor,
        IUsageProviderConfiguration configuration,
        HttpClient httpClient,
        Action<string>? logger,
        TimeSpan? requestTimeout,
        int maxResponseBodyBytes,
        IUsageProviderProcessRunner? processRunner = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? (_ => { });
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > MaximumCancellableTimeout)
        {
#pragma warning disable CA1512 // TimeSpan does not implement INumberBase required by ThrowIfNegativeOrZero.
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
#pragma warning restore CA1512
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBodyBytes);

        _maxResponseBodyBytes = maxResponseBodyBytes;
        _processRunner = processRunner ?? new BoundedUsageProviderProcessRunner();
    }

    public UsageProviderDescriptor Descriptor => _descriptor;

    public async Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_descriptor.Id.Equals("jetbrains", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetJetBrainsSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("kiro", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(
                token => KiroUsageProviderAdapter.GetSnapshotAsync(_descriptor, _configuration, _processRunner, token),
                cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetOllamaSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            || (_descriptor.Id.Equals("opencodego", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ResolveCredential().ApiKey)))
        {
            return await ExecuteWithDeadlineAsync(GetOpenCodeSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("minimax", StringComparison.OrdinalIgnoreCase))
        {
            var miniMaxCredential = ResolveCredential();
            if (string.IsNullOrWhiteSpace(miniMaxCredential.ApiKey)
                && !string.IsNullOrWhiteSpace(miniMaxCredential.CookieHeader))
            {
                return await ExecuteWithDeadlineAsync(
                    token => GetMiniMaxWebSnapshotAsync(miniMaxCredential, token),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (_descriptor.Id.Equals("kilo", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetKiloSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.AuthKind == UsageProviderAuthKind.Local)
        {
            return await ExecuteWithDeadlineAsync(
                token => LocalUsageProviderAdapter.GetSnapshotAsync(
                    _descriptor,
                    _configuration,
                    _httpClient,
                    CreateHttpFailure,
                    ReadBoundedResponseBytesAsync,
                    token),
                cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("zed", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetZedSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetOpenAiSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("amp", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetAmpSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetWindsurfSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("deepgram", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetDeepgramSnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("qwencloud", StringComparison.OrdinalIgnoreCase)
            || _descriptor.Id.Equals("alibabatokenplan", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetAlibabaGatewaySnapshotAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("t3chat", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteWithDeadlineAsync(GetT3ChatSnapshotAsync, cancellationToken).ConfigureAwait(false);
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

            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(_requestTimeout);
            try
            {
                using var request = CreateRequest(endpoint, credential);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add(new UsageProviderRequestException(
                        $"{endpoint.Name}: HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                        retryAfter: GetRetryAfter(response.Headers.RetryAfter),
                        statusCode: response.StatusCode));
                    continue;
                }

                var body = await ReadBoundedResponseBodyAsync(response.Content, requestCts.Token).ConfigureAwait(false);
                var snapshot = UsageJsonParser.ParseText(
                    _descriptor,
                    endpoint.Name,
                    body,
                    DateTimeOffset.UtcNow);
                snapshots.Add(snapshot);
                _logger($"[TokensLimits] Provider {_descriptor.Id}: snapshot fetched from {endpoint.Name}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                failures.Add(new UsageProviderRequestException(
                    $"{endpoint.Name}: request timed out after {_requestTimeout.TotalSeconds:0.#} seconds."));
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

        var lastRequestFailure = failures.OfType<UsageProviderRequestException>().LastOrDefault();
        throw new UsageProviderRequestException(
            $"Не удалось получить реальные данные {_descriptor.DisplayName}: {DescribeFailures(failures)}",
            failures.LastOrDefault(),
            lastRequestFailure?.RetryAfter,
            lastRequestFailure?.StatusCode);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        return null;
    }

    private static string SerializeAlibabaParameters(string apiName, Uri dashboardUrl, bool isQwen)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("Api", apiName);
            writer.WriteString("V", "1.0");
            writer.WriteStartObject("Data");
            writer.WriteStartObject("cornerstoneParam");
            writer.WriteString("feTraceId", Guid.NewGuid().ToString().ToLowerInvariant());
            writer.WriteString("feURL", dashboardUrl.AbsoluteUri);
            writer.WriteString("protocol", "V2");
            writer.WriteString("console", "ONE_CONSOLE");
            writer.WriteString("productCode", "p_efm");
            writer.WriteString("domain", dashboardUrl.Host);
            writer.WriteString("consoleSite", isQwen ? "QWENCLOUD" : "MODELSTUDIO_ALBABACLOUD");
            writer.WriteString("userNickName", string.Empty);
            writer.WriteString("userPrincipalName", string.Empty);
            writer.WriteString("xsp_lang", "en-US");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static UsageProviderRequestException CreateHttpFailure(
        string operation,
        HttpResponseMessage response)
        => new(
            $"{operation}: HTTP {(int)response.StatusCode} ({response.StatusCode}).",
            retryAfter: GetRetryAfter(response.Headers.RetryAfter),
            statusCode: response.StatusCode);

    private HttpRequestMessage CreateRequest(UsageProviderEndpoint endpoint, ResolvedCredential credential)
    {
        var url = ResolveUrl(endpoint);
        var request = new HttpRequestMessage(new HttpMethod(endpoint.HttpMethod), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
        }

        if (Descriptor.Id.Equals("kimi", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            var kimiAuth = Regex.Match(
                credential.CookieHeader,
                @"(?:^|;\s*)kimi-auth=([^;]+)",
                RegexOptions.IgnoreCase);
            if (kimiAuth.Success)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {kimiAuth.Groups[1].Value}");
            }
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

        if (Descriptor.Id.Equals("alibaba", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.ApiKey}");
            request.Headers.TryAddWithoutValidation("X-DashScope-API-Key", credential.ApiKey);
            var region = _configuration.GetValue(Descriptor.Id, "region");
            var isChina = region?.Equals("cn", StringComparison.OrdinalIgnoreCase) == true;
            var origin = isChina
                ? "https://bailian.console.aliyun.com"
                : "https://modelstudio.console.alibabacloud.com";
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("Referer", origin + (isChina ? "/cn-beijing/?tab=model" : "/ap-southeast-1/?tab=coding-plan"));
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
            request.Headers.TryAddWithoutValidation("User-Agent", ProductUserAgent);
        }

        return request;
    }

    private string ResolveRequestBody(UsageProviderEndpoint endpoint)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddDays(-30);
        return (endpoint.RequestBody ?? "{}")
            .Replace("{startTime}", FormatAnalyticsTimestamp(start), StringComparison.Ordinal)
            .Replace("{endTime}", FormatAnalyticsTimestamp(now), StringComparison.Ordinal)
            .Replace("{commodityCode}", ResolveAlibabaCommodityCode(), StringComparison.Ordinal);
    }

    private string ResolveAlibabaCommodityCode()
        => _configuration.GetValue(Descriptor.Id, "region")?.Equals("cn", StringComparison.OrdinalIgnoreCase) == true
            ? "sfm_codingplan_public_cn"
            : "sfm_codingplan_public_intl";

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
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            configuredBaseUri = ParseBaseUrl(baseUrl, Descriptor.DisplayName);
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
            if (!Descriptor.Id.Equals("alibaba", StringComparison.OrdinalIgnoreCase)
                || endpointUrl is null)
            {
                return endpointUrl;
            }

            var isChina = _configuration.GetValue(Descriptor.Id, "region")?.Equals("cn", StringComparison.OrdinalIgnoreCase) == true;
            return isChina
                ? endpointUrl
                    .Replace("modelstudio.console.alibabacloud.com", "bailian.console.aliyun.com", StringComparison.OrdinalIgnoreCase)
                    .Replace("ap-southeast-1", "cn-beijing", StringComparison.OrdinalIgnoreCase)
                : endpointUrl;
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
        ValidateEndpointUri(builder.Uri, Descriptor.DisplayName);
        return builder.Uri;
    }

    private async Task<UsageSnapshot> GetOpenCodeSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            throw new UsageProviderConfigurationException(
                $"Для {Descriptor.DisplayName} нужен Cookie-заголовок авторизованной сессии.");
        }

        var workspaceId = _configuration.GetValue(Descriptor.Id, "workspaceId")
            ?? _configuration.GetValue(Descriptor.Id, "accountId");
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            var workspaceText = await GetOpenCodeServerTextAsync(
                "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f",
                null,
                credential.CookieHeader,
                new Uri("https://opencode.ai/"),
                cancellationToken).ConfigureAwait(false);
            workspaceId = Regex.Match(workspaceText, @"wrk_[A-Za-z0-9]+", RegexOptions.CultureInvariant).Value;
        }

        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new UsageProviderRequestException(
                $"{Descriptor.DisplayName}: workspace ID не найден в авторизованном аккаунте.");
        }

        var now = DateTimeOffset.UtcNow;
        var source = "https://opencode.ai";
        string raw;
        if (Descriptor.Id.Equals("opencodego", StringComparison.OrdinalIgnoreCase))
        {
            var pageUri = new Uri($"https://opencode.ai/workspace/{Uri.EscapeDataString(workspaceId)}/go");
            raw = await GetOpenCodePageTextAsync(pageUri, credential.CookieHeader, cancellationToken).ConfigureAwait(false);
            source = pageUri.AbsoluteUri;
        }
        else
        {
            raw = await GetOpenCodeServerTextAsync(
                "7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4",
                workspaceId,
                credential.CookieHeader,
                new Uri($"https://opencode.ai/workspace/{Uri.EscapeDataString(workspaceId)}/billing"),
                cancellationToken).ConfigureAwait(false);
            source = "https://opencode.ai/_server";
        }

        var primary = ParseOpenCodeWindow(raw, "rollingUsage", now, 5 * 60 * 60);
        var secondary = ParseOpenCodeWindow(raw, "weeklyUsage", now, 7 * 24 * 60 * 60);
        var metrics = new List<UsageMetric>
        {
            new("Workspace", workspaceId),
        };
        AddOpenCodeNumberMetric(raw, metrics, "monthlyUsageUSD", "Monthly usage", "USD");
        AddOpenCodeNumberMetric(raw, metrics, "monthlyLimitUSD", "Monthly limit", "USD");
        AddOpenCodeNumberMetric(raw, metrics, "balanceUSD", "Balance", "USD");

        if (primary is null && secondary is null)
        {
            try
            {
                var parsed = UsageJsonParser.ParseText(Descriptor, source, raw, now);
                if (parsed.PrimaryWindow is not null || parsed.SecondaryWindow is not null || parsed.Metrics.Count > 0)
                {
                    _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from OpenCode payload.");
                    return parsed with { Source = source };
                }
            }
            catch (UsageProviderRequestException)
            {
                // OpenCode server functions commonly return JavaScript-like payloads;
                // the locked rolling/weekly fields are parsed below when it is not JSON.
            }
        }

        if (primary is null && secondary is null && metrics.Count == 1)
        {
            throw new UsageProviderRequestException(
                $"{Descriptor.DisplayName}: payload не содержит rolling/weekly usage данных.");
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from OpenCode usage payload.");
        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            primary,
            secondary,
            null,
            false)
        {
            FetchedAt = now,
            Source = source,
            Metrics = metrics,
        };
    }

    private async Task<string> GetOpenCodeServerTextAsync(
        string serverId,
        string? workspaceId,
        string cookieHeader,
        Uri referer,
        CancellationToken cancellationToken)
    {
        var builder = new UriBuilder("https://opencode.ai/_server");
        var query = new List<string> { $"id={Uri.EscapeDataString(serverId)}" };
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            query.Add($"args={Uri.EscapeDataString($"[\"{workspaceId}\"]")}");
        }

        builder.Query = string.Join('&', query);
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("X-Server-Id", serverId);
        request.Headers.TryAddWithoutValidation("X-Server-Instance", $"server-fn:{Guid.NewGuid():D}");
        request.Headers.TryAddWithoutValidation("Origin", "https://opencode.ai");
        request.Headers.TryAddWithoutValidation("Referer", referer.AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Accept", "text/javascript, application/json;q=0.9, */*;q=0.8");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("OpenCode server function", response);
        }

        return await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetOpenCodePageTextAsync(
        Uri uri,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Origin", "https://opencode.ai");
        request.Headers.TryAddWithoutValidation("Referer", "https://opencode.ai/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("OpenCode page", response);
        }

        return await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static UsageWindow? ParseOpenCodeWindow(
        string raw,
        string key,
        DateTimeOffset now,
        int fallbackWindowSeconds)
    {
        var match = Regex.Match(
            raw,
            $@"(?is)[""']?{Regex.Escape(key)}[""']?\s*[:=].*?[""']?(?:usagePercent|usedPercent|percentUsed|percent)[""']?\s*[:=]\s*(?<percent>[0-9]+(?:\.[0-9]+)?).*?[""']?(?:resetInSec|resetInSeconds|resetSeconds|reset_sec|reset_in_sec)[""']?\s*[:=]\s*(?<reset>[0-9]+)",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || !int.TryParse(match.Groups["reset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetSeconds)
            || resetSeconds < 0)
        {
            return null;
        }

        return new UsageWindow(Math.Clamp(percent, 0, 100), now.AddSeconds(resetSeconds), fallbackWindowSeconds);
    }

    private async Task<UsageSnapshot> GetMiniMaxWebSnapshotAsync(
        ResolvedCredential credential,
        CancellationToken cancellationToken)
    {
        var endpoint = UsageProviderEndpointCatalog
            .For(Descriptor.Id)
            .Single(item => item.Name.Equals("web-coding-plan", StringComparison.OrdinalIgnoreCase));
        using var request = CreateRequest(endpoint, credential);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("MiniMax web", response);
        }

        var body = await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (TryParseMiniMaxWebJson(body, now, out var snapshot))
        {
            _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from web coding plan.");
            return snapshot;
        }

        if (TryParseMiniMaxWebText(body, now, out snapshot))
        {
            _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from web coding plan text.");
            return snapshot;
        }

        throw new UsageProviderRequestException(
            "MiniMax web: страница не содержит реальных данных coding plan.");
    }

    private bool TryParseMiniMaxWebJson(string body, DateTimeOffset now, out UsageSnapshot snapshot)
    {
        snapshot = default!;
        var script = Regex.Match(
            body,
            @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>(?<json>.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!script.Success)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(script.Groups["json"].Value);
            JsonElement? modelRemains = null;
            foreach (var item in EnumerateJsonObjects(document.RootElement))
            {
                if (item.TryGetProperty("model_remains", out var remains)
                    && remains.ValueKind == JsonValueKind.Array)
                {
                    modelRemains = remains.Clone();
                    break;
                }
            }
            if (modelRemains is null)
            {
                return false;
            }

            var metrics = new List<UsageMetric>();
            var plan = FindMiniMaxPlanName(document.RootElement);
            if (!string.IsNullOrWhiteSpace(plan))
            {
                metrics.Add(new UsageMetric("Plan", plan));
            }

            UsageWindow? primary = null;
            UsageWindow? secondary = null;
            foreach (var item in modelRemains.Value.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object))
            {
                var modelName = TryGetJsonStringAny(item, "model_name", "modelName");
                if (!string.IsNullOrWhiteSpace(modelName))
                {
                    metrics.Add(new UsageMetric("Model", modelName));
                }

                AddMiniMaxQuotaMetrics(item, metrics, "current_interval");
                AddMiniMaxQuotaMetrics(item, metrics, "current_weekly");
                primary ??= CreateMiniMaxWindow(item, "current_interval", now, 5 * 60 * 60);
                secondary ??= CreateMiniMaxWindow(item, "current_weekly", now, 7 * 24 * 60 * 60);
            }

            if (primary is null && secondary is null)
            {
                return false;
            }

            snapshot = new UsageSnapshot(
                Descriptor.Id,
                Descriptor.DisplayName,
                primary,
                secondary,
                plan,
                false)
            {
                FetchedAt = now,
                Source = "https://platform.minimax.io/user-center/payment/coding-plan?cycle_type=3",
                Metrics = metrics,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryParseMiniMaxWebText(string body, DateTimeOffset now, out UsageSnapshot snapshot)
    {
        snapshot = default!;
        var remaining = MatchMiniMaxNumber(body, "current_interval_remaining_percent");
        var total = MatchMiniMaxNumber(body, "current_interval_total_count");
        var remainingCount = MatchMiniMaxNumber(body, "current_interval_usage_count");
        var resetAt = MatchMiniMaxDate(body, "end_time");
        if (remaining is null && (total is null || remainingCount is null))
        {
            return false;
        }

        if (remaining is null && total > 0)
        {
            remaining = remainingCount / total * 100;
        }

        var metrics = new List<UsageMetric>();
        var plan = MatchMiniMaxString(body, "plan_name")
            ?? MatchMiniMaxString(body, "current_subscribe_title");
        if (!string.IsNullOrWhiteSpace(plan))
        {
            metrics.Add(new UsageMetric("Plan", plan));
        }

        if (total is not null)
        {
            metrics.Add(new UsageMetric("Quota total", FormatNumber(total.Value), "requests", Limit: total));
        }

        if (remainingCount is not null)
        {
            metrics.Add(new UsageMetric("Quota remaining", FormatNumber(remainingCount.Value), "requests", Remaining: remainingCount));
        }

        if (remaining is null || resetAt is null)
        {
            return false;
        }

        snapshot = new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            new UsageWindow(Math.Clamp(100 - remaining.Value, 0, 100), resetAt.Value, 5 * 60 * 60),
            null,
            plan,
            false)
        {
            FetchedAt = now,
            Source = "https://platform.minimax.io/user-center/payment/coding-plan?cycle_type=3",
            Metrics = metrics,
        };
        return true;
    }

    private static UsageWindow? CreateMiniMaxWindow(JsonElement item, string prefix, DateTimeOffset now, int windowSeconds)
    {
        var remaining = TryGetJsonDoubleAny(
            item,
            out var remainingPercent,
            $"{prefix}_remaining_percent");
        var total = TryGetJsonDoubleAny(item, out var totalCount, $"{prefix}_total_count");
        var remainingCount = TryGetJsonDoubleAny(item, out var remainingCountValue, $"{prefix}_usage_count");
        if (!remaining && total && remainingCount && totalCount > 0)
        {
            remainingPercent = remainingCountValue / totalCount * 100;
            remaining = true;
        }

        var reset = TryGetJsonDateAny(item, $"{prefix}_end_time", $"{prefix}_endTime", "end_time", "endTime");
        return remaining && reset is not null
            ? new UsageWindow(Math.Clamp(100 - remainingPercent, 0, 100), reset.Value, windowSeconds)
            : null;
    }

    private static void AddMiniMaxQuotaMetrics(JsonElement item, List<UsageMetric> metrics, string prefix)
    {
        if (TryGetJsonDoubleAny(item, out var total, $"{prefix}_total_count"))
        {
            metrics.Add(new UsageMetric($"{prefix} total", FormatNumber(total), "requests", Limit: total));
        }

        if (TryGetJsonDoubleAny(item, out var remaining, $"{prefix}_usage_count"))
        {
            metrics.Add(new UsageMetric($"{prefix} remaining", FormatNumber(remaining), "requests", Remaining: remaining));
        }
    }

    private static string? FindMiniMaxPlanName(JsonElement root)
    {
        foreach (var item in EnumerateJsonObjects(root))
        {
            foreach (var property in item.EnumerateObject())
            {
                if ((property.Name.Equals("plan_name", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("planName", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("current_subscribe_title", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("currentSubscribeTitle", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }

    private static double? MatchMiniMaxNumber(string body, string key)
    {
        var match = Regex.Match(
            body,
            $@"[""']?{Regex.Escape(key)}[""']?\s*:\s*[""']?(?<value>-?[0-9]+(?:\.[0-9]+)?)[""']?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static DateTimeOffset? MatchMiniMaxDate(string body, string key)
    {
        var match = Regex.Match(
            body,
            $@"[""']?{Regex.Escape(key)}[""']?\s*:\s*[""']?(?<value>[0-9]{{10,13}})[""']?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return FromUnixTime(value);
    }

    private static string? MatchMiniMaxString(string body, string key)
    {
        var match = Regex.Match(
            body,
            $@"[""']?{Regex.Escape(key)}[""']?\s*:\s*[""'](?<value>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static void AddOpenCodeNumberMetric(
        string raw,
        List<UsageMetric> metrics,
        string key,
        string name,
        string unit)
    {
        var match = Regex.Match(
            raw,
            $@"(?is)[""']?{Regex.Escape(key)}[""']?\s*[:=]\s*[""']?(?<value>-?[0-9]+(?:\.[0-9]+)?)[""']?",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            metrics.Add(new UsageMetric(name, match.Groups["value"].Value, unit));
        }
    }

    private async Task<UsageSnapshot> GetKiloSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        var apiKey = credential.ApiKey
            ?? ReadKiloAuthToken();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new UsageProviderConfigurationException(
                "Для Kilo нужен API-ключ или локальная авторизация Kilo CLI.");
        }

        var configuredBaseUrl = _configuration.GetValue(Descriptor.Id, "baseUrl");
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? new Uri("https://app.kilo.ai/api/trpc/", UriKind.Absolute)
            : ParseBaseUrl(configuredBaseUrl.TrimEnd('/') + "/", Descriptor.DisplayName);
        var procedures = new[]
        {
            "user.getCreditBlocks",
            "kiloPass.getState",
            "user.getAutoTopUpPaymentMethod",
        };
        var procedurePath = string.Join(',', procedures);
        var input = "{" + string.Join(',', procedures.Select((_, index) => $"\"{index}\":{{\"json\":null}}")) + "}";
        var builder = new UriBuilder(new Uri(baseUrl, procedurePath))
        {
            Query = $"batch=1&input={Uri.EscapeDataString(input)}",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var organization = _configuration.GetValue(Descriptor.Id, "accountId");
        if (!string.IsNullOrWhiteSpace(organization))
        {
            request.Headers.TryAddWithoutValidation("X-KILOCODE-ORGANIZATIONID", organization);
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("Kilo tRPC", response);
        }

        var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var creditObjects = EnumerateJsonObjects(root)
            .Where(item => item.TryGetProperty("amount_mUsd", out _)
                || item.TryGetProperty("balance_mUsd", out _))
            .ToArray();
        var total = creditObjects
            .Select(item => TryGetJsonDouble(item, "amount_mUsd", out var value) ? value / 1_000_000 : 0)
            .Where(value => value >= 0)
            .Sum();
        var remaining = creditObjects
            .Select(item => TryGetJsonDouble(item, "balance_mUsd", out var value) ? value / 1_000_000 : 0)
            .Where(value => value >= 0)
            .Sum();
        var hasCreditValues = creditObjects.Any(item => item.TryGetProperty("amount_mUsd", out _)
            || item.TryGetProperty("balance_mUsd", out _));

        if (!hasCreditValues)
        {
            var creditContext = EnumerateJsonObjects(root).FirstOrDefault(item =>
                HasAnyJsonProperty(item, "creditsTotal", "totalCredits", "creditsRemaining", "remainingCredits", "creditsUsed"));
            if (creditContext.ValueKind == JsonValueKind.Object)
            {
                var hasTotal = TryGetJsonDoubleAny(creditContext, out var fallbackTotal, "creditsTotal", "totalCredits", "total", "limit");
                var hasRemaining = TryGetJsonDoubleAny(creditContext, out var fallbackRemaining, "creditsRemaining", "remainingCredits", "remaining", "balance");
                var hasUsed = TryGetJsonDoubleAny(creditContext, out var fallbackUsed, "creditsUsed", "usedCredits", "used", "spent");
                total = hasTotal ? fallbackTotal : hasUsed && hasRemaining ? fallbackUsed + fallbackRemaining : 0;
                remaining = hasRemaining ? fallbackRemaining : hasTotal && hasUsed ? fallbackTotal - fallbackUsed : 0;
                hasCreditValues = hasTotal || hasRemaining || hasUsed;
            }
        }

        var passObject = EnumerateJsonObjects(root).FirstOrDefault(item =>
            HasAnyJsonProperty(item, "currentPeriodBaseCreditsUsd", "currentPeriodBonusCreditsUsd", "currentPeriodUsageUsd"));
        var hasPass = passObject.ValueKind == JsonValueKind.Object;
        var passBase = hasPass && TryGetJsonDouble(passObject, "currentPeriodBaseCreditsUsd", out var baseCredits)
            ? Math.Max(0, baseCredits)
            : 0;
        var passBonus = hasPass && TryGetJsonDouble(passObject, "currentPeriodBonusCreditsUsd", out var bonusCredits)
            ? Math.Max(0, bonusCredits)
            : 0;
        var passUsed = hasPass && TryGetJsonDouble(passObject, "currentPeriodUsageUsd", out var usedCredits)
            ? Math.Max(0, usedCredits)
            : (double?)null;
        var passTotal = hasPass ? passBase + passBonus : (double?)null;
        var passReset = hasPass
            ? TryGetJsonDateAny(passObject, "nextBillingAt", "nextRenewalAt", "renewsAt", "renewAt")
            : null;
        var plan = hasPass
            ? TryGetJsonStringAny(passObject, "tier", "planName", "passName", "subscriptionName")
            : null;

        var metrics = new List<UsageMetric>();
        if (hasCreditValues)
        {
            total = Math.Max(0, total);
            remaining = Math.Max(0, remaining);
            metrics.Add(new UsageMetric("Credits total", FormatNumber(total), "USD", Limit: total, Remaining: remaining));
            metrics.Add(new UsageMetric("Credits used", FormatNumber(Math.Max(0, total - remaining)), "USD", Used: Math.Max(0, total - remaining)));
            metrics.Add(new UsageMetric("Credits remaining", FormatNumber(remaining), "USD", Remaining: remaining));
        }

        UsageWindow? primary = null;
        if (hasPass && passTotal is > 0 && passUsed is not null && passReset is not null)
        {
            primary = new UsageWindow(
                Math.Clamp(passUsed.Value / passTotal.Value * 100, 0, 100),
                passReset.Value,
                30 * 24 * 60 * 60);
            metrics.Add(new UsageMetric("Kilo Pass", FormatNumber(passUsed.Value), "USD", Used: passUsed, Limit: passTotal, ResetAt: passReset));
        }

        if (passBonus > 0)
        {
            metrics.Add(new UsageMetric("Pass bonus", FormatNumber(passBonus), "USD", Remaining: passBonus));
        }

        var autoTopUp = EnumerateJsonObjects(root).FirstOrDefault(item =>
            HasAnyJsonProperty(item, "autoTopUpEnabled", "isEnabled", "enabled", "paymentMethod"));
        if (autoTopUp.ValueKind == JsonValueKind.Object
            && TryGetJsonBoolAny(autoTopUp, out var autoTopUpEnabled, "autoTopUpEnabled", "isEnabled", "enabled"))
        {
            metrics.Add(new UsageMetric("Auto top-up", autoTopUpEnabled ? "enabled" : "disabled"));
        }

        if (!hasCreditValues && !hasPass && metrics.Count == 0)
        {
            throw new UsageProviderRequestException("Kilo tRPC не вернул credit blocks или Kilo Pass.");
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from Kilo tRPC batch.");
        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            primary,
            null,
            string.IsNullOrWhiteSpace(plan) ? "Kilo" : plan,
            false)
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Source = "app.kilo.ai/api/trpc/user.getCreditBlocks,kiloPass.getState,user.getAutoTopUpPaymentMethod",
            Metrics = metrics,
        };
    }

    private string? ReadKiloAuthToken()
    {
        var configuredPath = _configuration.GetValue(Descriptor.Id, "dataPath");
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "kilo", "auth.json")
            : configuredPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("kilo", out var kilo)
                && kilo.ValueKind == JsonValueKind.Object
                && TryGetJsonString(kilo, "access", out var access)
                ? access
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> EnumerateJsonObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateJsonObjects(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateJsonObjects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool HasAnyJsonProperty(JsonElement objectElement, params string[] names)
        => objectElement.ValueKind == JsonValueKind.Object
            && names.Any(name => objectElement.TryGetProperty(name, out _));

    private static bool TryGetJsonDoubleAny(JsonElement objectElement, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetJsonDouble(objectElement, name, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetJsonBoolAny(JsonElement objectElement, out bool value, params string[] names)
    {
        foreach (var name in names)
        {
            if (objectElement.TryGetProperty(name, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.GetBoolean();
                return true;
            }
        }

        value = false;
        return false;
    }

    private static string? TryGetJsonStringAny(JsonElement objectElement, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetJsonString(objectElement, name, out var value))
            {
                return value;
            }
        }

        return null;
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

    private static DateTimeOffset? TryGetJsonDateAny(JsonElement objectElement, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetJsonDate(objectElement, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private async Task<UsageSnapshot> GetOllamaSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        var configuredBaseUrl = _configuration.GetValue(Descriptor.Id, "baseUrl");
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? new Uri("https://ollama.com", UriKind.Absolute)
            : ParseBaseUrl(configuredBaseUrl, Descriptor.DisplayName);

        if (!string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            var settingsUri = new Uri(baseUrl, "/settings");
            using var request = new HttpRequestMessage(HttpMethod.Get, settingsUri);
            request.Headers.TryAddWithoutValidation("Cookie", credential.CookieHeader);
            request.Headers.TryAddWithoutValidation("Origin", baseUrl.GetLeftPart(UriPartial.Authority));
            request.Headers.TryAddWithoutValidation("Referer", settingsUri.AbsoluteUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var html = await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                try
                {
                    var snapshot = ParseOllamaCloudHtml(html, DateTimeOffset.UtcNow, settingsUri.AbsoluteUri);
                    _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from cloud settings.");
                    return snapshot;
                }
                catch (UsageProviderRequestException) when (!string.IsNullOrWhiteSpace(credential.ApiKey))
                {
                    // A stale web session may coexist with a valid API key. Try the supported API
                    // catalog before surfacing the web parser error.
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            var tagsUri = new Uri(baseUrl, "/api/tags");
            using var request = new HttpRequestMessage(HttpMethod.Get, tagsUri);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.ApiKey}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure("Ollama API", response);
            }

            var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var snapshot = UsageJsonParser.ParseOllama(Descriptor, document.RootElement, DateTimeOffset.UtcNow);
            _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from cloud model catalog.");
            return snapshot with { Source = tagsUri.AbsoluteUri, Plan = "Ollama Cloud" };
        }

        var localUri = new Uri("http://127.0.0.1:11434/api/tags", UriKind.Absolute);
        using (var request = new HttpRequestMessage(HttpMethod.Get, localUri))
        using (var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure("Локальный Ollama API", response);
            }

            var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var snapshot = UsageJsonParser.ParseOllama(Descriptor, document.RootElement, DateTimeOffset.UtcNow);
            _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from local model catalog.");
            return snapshot with { Source = localUri.AbsoluteUri };
        }
    }

    private static UsageSnapshot ParseOllamaCloudHtml(string html, DateTimeOffset fetchedAt, string source)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new UsageProviderRequestException("Ollama cloud settings вернули пустой ответ.");
        }

        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        var primary = ParseOllamaHtmlWindow(text, ["Session usage", "Hourly usage"], 5 * 60 * 60, fetchedAt);
        var secondary = ParseOllamaHtmlWindow(text, ["Weekly usage"], 7 * 24 * 60 * 60, fetchedAt);
        var metrics = new List<UsageMetric>();
        if (primary is null && secondary is null)
        {
            var percent = Regex.Match(text, @"(?i)(?<value>\d+(?:\.\d+)?)\s*%\s*(?:used|remaining)");
            if (percent.Success)
            {
                metrics.Add(new UsageMetric("Cloud usage", percent.Groups["value"].Value, "%"));
            }
        }

        var plan = Regex.Match(text, @"(?im)^\s*(?:plan|tier)\s*:?\s*(?<value>[A-Za-z][^\r\n]{1,80})")
            .Groups["value"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(plan))
        {
            metrics.Add(new UsageMetric("Plan", plan));
        }

        if (primary is null && secondary is null && metrics.Count == 0)
        {
            throw new UsageProviderRequestException(
                "Ollama cloud settings не содержат опубликованных Session/Weekly usage данных.");
        }

        return new UsageSnapshot("ollama", "Ollama", primary, secondary, plan, false)
        {
            FetchedAt = fetchedAt,
            Source = source,
            Metrics = metrics,
        };
    }

    private static UsageWindow? ParseOllamaHtmlWindow(
        string text,
        IReadOnlyList<string> labels,
        int windowSeconds,
        DateTimeOffset fetchedAt)
    {
        var labelIndex = -1;
        var labelLength = 0;
        foreach (var label in labels)
        {
            var index = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (labelIndex < 0 || index < labelIndex))
            {
                labelIndex = index;
                labelLength = label.Length;
            }
        }

        if (labelIndex < 0)
        {
            return null;
        }

        var end = text.Length;
        foreach (var boundary in new[] { "Session usage", "Hourly usage", "Weekly usage" })
        {
            var boundaryIndex = text.IndexOf(boundary, labelIndex + labelLength, StringComparison.OrdinalIgnoreCase);
            if (boundaryIndex >= 0 && boundaryIndex < end)
            {
                end = boundaryIndex;
            }
        }

        var block = text[(labelIndex + labelLength)..end];
        var usedMatch = Regex.Match(block, @"(?i)(?<value>\d+(?:\.\d+)?)\s*%\s*used");
        var remainingMatch = Regex.Match(block, @"(?i)(?<value>\d+(?:\.\d+)?)\s*%\s*(?:remaining|left)");
        double? usedPercent = usedMatch.Success
            ? ParseFlexibleNumber(usedMatch.Groups["value"].Value)
            : remainingMatch.Success
                ? 100 - ParseFlexibleNumber(remainingMatch.Groups["value"].Value)
                : null;
        if (usedPercent is null)
        {
            return null;
        }

        var resetMatch = Regex.Match(
            block,
            @"(?<date>20\d{2}-\d{2}-\d{2}(?:[T ][0-9:.+\-Z]+)?)",
            RegexOptions.IgnoreCase);
        var resetAt = resetMatch.Success
            && DateTimeOffset.TryParse(
                resetMatch.Groups["date"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : (DateTimeOffset?)null;
        if (resetAt is null)
        {
            return null;
        }

        return new UsageWindow(Math.Clamp(usedPercent.Value, 0, 100), resetAt.Value, windowSeconds);
    }

    private static Uri ParseBaseUrl(string value, string displayName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new UsageProviderConfigurationException($"Базовый URL {displayName} имеет неверный формат.");
        }

        ValidateEndpointUri(uri, displayName);
        return uri;
    }

    private static void ValidateEndpointUri(Uri uri, string displayName)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new UsageProviderConfigurationException($"Базовый URL {displayName} имеет неверный формат.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new UsageProviderConfigurationException(
                $"Базовый URL {displayName} должен использовать HTTPS; HTTP разрешён только для локального сервера.");
        }
    }

    private async Task<UsageSnapshot> GetJetBrainsSnapshotAsync(CancellationToken cancellationToken)
    {
        var quotaFile = ResolveJetBrainsQuotaFile();
        if (quotaFile is null)
        {
            throw new UsageProviderConfigurationException(
                "JetBrains AI quota-файл не найден. Запустите AI Assistant или укажите путь к AIAssistantQuotaManager2.xml в настройках.");
        }

        var raw = await File.ReadAllTextAsync(quotaFile, cancellationToken).ConfigureAwait(false);
        XDocument document;
        try
        {
            document = XDocument.Parse(raw, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new UsageProviderRequestException(
                $"JetBrains AI: quota-файл имеет неверный XML ({exception.Message}).",
                exception);
        }

        var component = document
            .Descendants("component")
            .FirstOrDefault(element => string.Equals(
                (string?)element.Attribute("name"),
                "AIAssistantQuotaManager2",
                StringComparison.Ordinal));
        if (component is null)
        {
            throw new UsageProviderRequestException(
                "JetBrains AI: в quota-файле отсутствует AIAssistantQuotaManager2.");
        }

        var quotaJson = component
            .Elements("option")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "quotaInfo", StringComparison.Ordinal))
            ?.Attribute("value")?.Value;
        if (string.IsNullOrWhiteSpace(quotaJson))
        {
            throw new UsageProviderRequestException(
                "JetBrains AI: quotaInfo отсутствует в AIAssistantQuotaManager2.");
        }

        using var quotaDocument = JsonDocument.Parse(quotaJson);
        var quota = quotaDocument.RootElement;
        var current = TryGetJsonDouble(quota, "current", out var currentValue) ? currentValue : (double?)null;
        var maximum = TryGetJsonDouble(quota, "maximum", out var maximumValue) ? maximumValue : (double?)null;
        double? available = null;
        if (quota.TryGetProperty("tariffQuota", out var tariffQuota)
            && tariffQuota.ValueKind == JsonValueKind.Object
            && TryGetJsonDouble(tariffQuota, "available", out var availableValue))
        {
            available = availableValue;
        }

        var nextRefillJson = component
            .Elements("option")
            .FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "nextRefill", StringComparison.Ordinal))
            ?.Attribute("value")?.Value;
        DateTimeOffset? resetAt = TryGetJsonDate(quota, "until");
        if (!string.IsNullOrWhiteSpace(nextRefillJson))
        {
            try
            {
                using var refillDocument = JsonDocument.Parse(nextRefillJson);
                resetAt = TryGetJsonDate(refillDocument.RootElement, "next") ?? resetAt;
            }
            catch (JsonException)
            {
                // quotaInfo remains authoritative when the optional refill blob is malformed.
            }
        }

        var metrics = new List<UsageMetric>();
        var quotaType = TryGetJsonString(quota, "type");
        if (!string.IsNullOrWhiteSpace(quotaType))
        {
            metrics.Add(new UsageMetric("Quota type", quotaType));
        }

        if (current is not null)
        {
            metrics.Add(new UsageMetric("Credits used", FormatNumber(Math.Max(0, current.Value)), "credits", Used: current));
        }

        if (maximum is not null)
        {
            metrics.Add(new UsageMetric("Credits total", FormatNumber(Math.Max(0, maximum.Value)), "credits", Limit: maximum));
        }

        if (available is not null)
        {
            metrics.Add(new UsageMetric("Credits remaining", FormatNumber(Math.Max(0, available.Value)), "credits", Remaining: available));
        }

        UsageWindow? primary = null;
        if (current is not null && maximum is > 0 && resetAt is not null)
        {
            primary = new UsageWindow(
                Math.Clamp(current.Value / maximum.Value * 100, 0, 100),
                resetAt.Value,
                0);
        }

        if (metrics.Count == 0)
        {
            throw new UsageProviderRequestException(
                "JetBrains AI: quotaInfo не содержит числовых данных.");
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from {quotaFile}.");
        return new UsageSnapshot(
            Descriptor.Id,
            Descriptor.DisplayName,
            primary,
            null,
            quotaType,
            false)
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Source = quotaFile,
            Metrics = metrics,
        };
    }

    private string? ResolveJetBrainsQuotaFile()
    {
        var configured = _configuration.GetValue(Descriptor.Id, "dataPath");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim();
            if (Directory.Exists(path))
            {
                path = Path.Combine(path, "options", "AIAssistantQuotaManager2.xml");
            }

            return File.Exists(path) ? path : null;
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        };
        var prefixes = new[]
        {
            "IntelliJIdea", "PyCharm", "WebStorm", "GoLand", "CLion", "DataGrip", "RubyMine", "Rider",
            "PhpStorm", "AppCode", "Fleet", "AndroidStudio", "RustRover", "Aqua", "DataSpell",
        };
        var candidates = new List<string>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    var quotaPath = Path.Combine(directory, "options", "AIAssistantQuotaManager2.xml");
                    if (File.Exists(quotaPath))
                    {
                        candidates.Add(quotaPath);
                    }
                }
            }
        }

        return candidates
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string? TryGetJsonString(JsonElement objectElement, string propertyName)
        => objectElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? TryGetJsonDate(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var raw = property.GetString();
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            {
                return FromUnixTime(numeric);
            }
        }
        else if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return FromUnixTime(number);
        }

        return null;
    }

    private static DateTimeOffset? FromUnixTime(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        return value > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
            : DateTimeOffset.FromUnixTimeSeconds((long)value);
    }

    private static double ParseFlexibleNumber(string raw)
        => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private async Task<UsageSnapshot> GetZedSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        var userId = _configuration.GetValue(Descriptor.Id, "userId")
            ?? Environment.GetEnvironmentVariable("ZED_USER_ID");
        if (string.IsNullOrWhiteSpace(credential.ApiKey) || string.IsNullOrWhiteSpace(userId))
        {
            throw new UsageProviderConfigurationException(
                "Для Zed задайте access token и user ID авторизованного аккаунта.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://cloud.zed.dev/client/users/me");
        request.Headers.TryAddWithoutValidation("Authorization", $"{userId} {credential.ApiKey}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("Zed profile", response);
        }

        var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var metrics = new List<UsageMetric>();
        var plan = root.TryGetProperty("plan", out var planObject) && planObject.ValueKind == JsonValueKind.Object
            ? planObject
            : default;
        var planName = plan.ValueKind == JsonValueKind.Object && plan.TryGetProperty("plan_v3", out var planValue)
            ? planValue.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(planName))
        {
            metrics.Add(new UsageMetric("Plan", planName));
        }

        DateTimeOffset? resetAt = null;
        if (plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("subscription_period", out var subscription)
            && subscription.ValueKind == JsonValueKind.Object
            && subscription.TryGetProperty("ended_at", out var endedAt)
            && endedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(endedAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEndedAt))
        {
            resetAt = parsedEndedAt;
            metrics.Add(new UsageMetric("Billing cycle ends", parsedEndedAt.ToString("O", CultureInfo.InvariantCulture), ResetAt: parsedEndedAt));
        }

        UsageWindow? primary = null;
        if (plan.ValueKind == JsonValueKind.Object
            && plan.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("edit_predictions", out var editPredictions)
            && editPredictions.ValueKind == JsonValueKind.Object
            && TryGetJsonDouble(editPredictions, "used", out var used))
        {
            if (editPredictions.TryGetProperty("limit", out var limitValue)
                && limitValue.ValueKind == JsonValueKind.Number
                && limitValue.TryGetDouble(out var limit)
                && limit > 0)
            {
                var remaining = Math.Max(0, limit - used);
                metrics.Add(new UsageMetric("Edit predictions", FormatNumber(used), "requests", Used: used, Limit: limit, Remaining: remaining, ResetAt: resetAt));
                if (resetAt is not null)
                {
                    primary = new UsageWindow(Math.Clamp(used / limit * 100, 0, 100), resetAt.Value, 0);
                }
            }
            else if (limitValue.ValueKind == JsonValueKind.String
                && limitValue.GetString()?.Equals("unlimited", StringComparison.OrdinalIgnoreCase) == true)
            {
                metrics.Add(new UsageMetric("Edit predictions", FormatNumber(used), "requests", Used: used));
            }
        }

        if (metrics.Count == 0)
        {
            throw new UsageProviderRequestException("Zed profile не содержит plan/usage данных.");
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from cloud profile API.");
        return new UsageSnapshot(Descriptor.Id, Descriptor.DisplayName, primary, null, planName, false)
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Source = "cloud.zed.dev/client/users/me",
            Metrics = metrics,
        };
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
        var baseUri = ParseBaseUrl(baseUrl, "OpenAI");

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
                throw CreateHttpFailure($"OpenAI {path}", response);
            }

            var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
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

    private async Task<UsageSnapshot> GetAmpSnapshotAsync(CancellationToken cancellationToken)
    {
        var endpoints = UsageProviderEndpointCatalog.For(Descriptor.Id);
        var credential = ResolveCredential();
        var endpoint = !string.IsNullOrWhiteSpace(credential.ApiKey) ? endpoints[0] : endpoints[1];
        if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            throw new UsageProviderConfigurationException(
                "Для Amp задайте AMP_API_KEY или session Cookie из ampcode.com.");
        }

        if (endpoint.RequiresCookie && string.IsNullOrWhiteSpace(credential.CookieHeader))
        {
            throw new UsageProviderConfigurationException(
                "Для веб-режима Amp задайте session Cookie из ampcode.com.");
        }

        using var request = CreateRequest(endpoint, credential);
        if (endpoint.Name.Equals("balance-web", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", "https://ampcode.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://ampcode.com/settings");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/143.0.0.0 Safari/537.36");
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure($"Amp {endpoint.Name}", response);
        }

        var body = await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var displayText = body;
        if (endpoint.Name.Equals("balance-api", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                displayText = document.RootElement
                    .GetProperty("result")
                    .GetProperty("displayText")
                    .GetString() ?? string.Empty;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new UsageProviderRequestException("Ответ Amp API не содержит result.displayText.", ex);
            }
        }

        var snapshot = AmpUsageDisplayParser.Parse(Descriptor, displayText, DateTimeOffset.UtcNow, endpoint.Name);
        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from {endpoint.Name}.");
        return snapshot;
    }

    private async Task<UsageSnapshot> GetWindsurfSnapshotAsync(CancellationToken cancellationToken)
    {
        var rawBundle = _configuration.GetValue(Descriptor.Id, "sessionBundle")
            ?? Environment.GetEnvironmentVariable("WINDSURF_SESSION_BUNDLE");
        if (string.IsNullOrWhiteSpace(rawBundle))
        {
            throw new UsageProviderConfigurationException(
                "Для Windsurf задайте session bundle с devin_session_token, devin_auth1_token, devin_account_id и devin_primary_org_id.");
        }

        var session = ParseWindsurfSession(rawBundle);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus");
        request.Headers.TryAddWithoutValidation("Content-Type", "application/proto");
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        request.Headers.TryAddWithoutValidation("Origin", "https://windsurf.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://windsurf.com/profile");
        request.Headers.TryAddWithoutValidation("x-auth-token", session.SessionToken);
        request.Headers.TryAddWithoutValidation("x-devin-session-token", session.SessionToken);
        request.Headers.TryAddWithoutValidation("x-devin-auth1-token", session.Auth1Token);
        request.Headers.TryAddWithoutValidation("x-devin-account-id", session.AccountId);
        request.Headers.TryAddWithoutValidation("x-devin-primary-org-id", session.PrimaryOrgId);
        request.Content = new ByteArrayContent(EncodeWindsurfRequest(session.SessionToken));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure("Windsurf GetPlanStatus", response);
        }

        var bytes = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var status = DecodeWindsurfResponse(bytes);
        var metrics = new List<UsageMetric>();
        if (!string.IsNullOrWhiteSpace(status.PlanName))
        {
            metrics.Add(new UsageMetric("Plan", status.PlanName));
        }

        if (status.PlanEnd is not null)
        {
            metrics.Add(new UsageMetric("Plan expires", status.PlanEnd.Value.ToString("O", CultureInfo.InvariantCulture), ResetAt: status.PlanEnd));
        }

        UsageWindow? primary = null;
        UsageWindow? secondary = null;
        if (status.DailyRemainingPercent is not null)
        {
            metrics.Add(new UsageMetric("Daily remaining", status.DailyRemainingPercent.Value.ToString(CultureInfo.InvariantCulture), "%", Remaining: status.DailyRemainingPercent));
            if (status.DailyResetAt is not null)
            {
                primary = new UsageWindow(100 - Math.Clamp(status.DailyRemainingPercent.Value, 0, 100), status.DailyResetAt.Value, 24 * 60 * 60);
            }
        }

        if (status.WeeklyRemainingPercent is not null)
        {
            metrics.Add(new UsageMetric("Weekly remaining", status.WeeklyRemainingPercent.Value.ToString(CultureInfo.InvariantCulture), "%", Remaining: status.WeeklyRemainingPercent));
            if (status.WeeklyResetAt is not null)
            {
                secondary = new UsageWindow(100 - Math.Clamp(status.WeeklyRemainingPercent.Value, 0, 100), status.WeeklyResetAt.Value, 7 * 24 * 60 * 60);
            }
        }

        if (status.GracePeriodStatus is not null)
        {
            metrics.Add(new UsageMetric("Grace period status", status.GracePeriodStatus.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (metrics.Count == 0)
        {
            throw new UsageProviderRequestException("Windsurf GetPlanStatus не содержит данных тарифа или квот.");
        }

        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from protobuf GetPlanStatus.");
        return new UsageSnapshot(Descriptor.Id, Descriptor.DisplayName, primary, secondary, status.PlanName, false)
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Source = "SeatManagementService/GetPlanStatus (protobuf)",
            Metrics = metrics,
        };
    }

    private static WindsurfSession ParseWindsurfSession(string raw)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        values[property.Name] = property.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            foreach (var segment in raw.Trim().Trim('{', '}').Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = segment.IndexOf('=');
                if (separator < 0) separator = segment.IndexOf(':');
                if (separator > 0)
                {
                    values[segment[..separator].Trim().Trim('"', '\'')] = segment[(separator + 1)..].Trim().Trim('"', '\'');
                }
            }
        }

        var sessionToken = FirstSessionValue(values, "devin_session_token", "devinSessionToken", "sessionToken");
        var auth1Token = FirstSessionValue(values, "devin_auth1_token", "devinAuth1Token", "auth1Token");
        var accountId = FirstSessionValue(values, "devin_account_id", "devinAccountId", "accountID", "accountId");
        var primaryOrgId = FirstSessionValue(values, "devin_primary_org_id", "devinPrimaryOrgId", "primaryOrgID", "primaryOrgId");
        if (string.IsNullOrWhiteSpace(sessionToken)
            || string.IsNullOrWhiteSpace(auth1Token)
            || string.IsNullOrWhiteSpace(accountId)
            || string.IsNullOrWhiteSpace(primaryOrgId))
        {
            throw new UsageProviderConfigurationException(
                "Windsurf session bundle должен содержать четыре значения Devin-сессии.");
        }

        return new WindsurfSession(sessionToken, auth1Token, accountId, primaryOrgId);
    }

    private static string? FirstSessionValue(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static byte[] EncodeWindsurfRequest(string sessionToken)
    {
        using var stream = new MemoryStream();
        WriteProtoVarint(stream, (1u << 3) | 2u);
        WriteProtoBytes(stream, Encoding.UTF8.GetBytes(sessionToken));
        WriteProtoVarint(stream, 2u << 3);
        WriteProtoVarint(stream, 1);
        return stream.ToArray();
    }

    private static WindsurfStatus DecodeWindsurfResponse(byte[] bytes)
    {
        var reader = new ProtobufReader(bytes);
        var status = new WindsurfStatus();
        while (reader.TryReadField(out var field, out var wireType))
        {
            if (field == 1 && wireType == 2)
            {
                DecodeWindsurfPlanStatus(reader.ReadBytes(), status);
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return status;
    }

    private static void DecodeWindsurfPlanStatus(byte[] bytes, WindsurfStatus status)
    {
        var reader = new ProtobufReader(bytes);
        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 2):
                    DecodeWindsurfPlanInfo(reader.ReadBytes(), status);
                    break;
                case (2, 2):
                    status.PlanStart = DecodeWindsurfTimestamp(reader.ReadBytes());
                    break;
                case (3, 2):
                    status.PlanEnd = DecodeWindsurfTimestamp(reader.ReadBytes());
                    break;
                case (12, 0):
                    status.GracePeriodStatus = checked((int)reader.ReadVarint());
                    break;
                case (14, 0):
                    status.DailyRemainingPercent = checked((int)reader.ReadVarint());
                    break;
                case (15, 0):
                    status.WeeklyRemainingPercent = checked((int)reader.ReadVarint());
                    break;
                case (17, 0):
                    status.DailyResetAt = DateTimeOffset.FromUnixTimeSeconds(checked((long)reader.ReadVarint()));
                    break;
                case (18, 0):
                    status.WeeklyResetAt = DateTimeOffset.FromUnixTimeSeconds(checked((long)reader.ReadVarint()));
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
    }

    private static void DecodeWindsurfPlanInfo(byte[] bytes, WindsurfStatus status)
    {
        var reader = new ProtobufReader(bytes);
        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0):
                    status.TeamsTier = checked((int)reader.ReadVarint());
                    break;
                case (2, 2):
                    status.PlanName = reader.ReadString();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
    }

    private static DateTimeOffset DecodeWindsurfTimestamp(byte[] bytes)
    {
        var reader = new ProtobufReader(bytes);
        long seconds = 0;
        long nanos = 0;
        while (reader.TryReadField(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0): seconds = checked((long)reader.ReadVarint()); break;
                case (2, 0): nanos = checked((long)reader.ReadVarint()); break;
                default: reader.Skip(wireType); break;
            }
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
    }

    private static void WriteProtoVarint(Stream stream, uint value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteProtoBytes(Stream stream, byte[] bytes)
    {
        WriteProtoVarint(stream, (uint)bytes.Length);
        stream.Write(bytes);
    }

    private sealed record WindsurfSession(string SessionToken, string Auth1Token, string AccountId, string PrimaryOrgId);

    private sealed class WindsurfStatus
    {
        public string? PlanName { get; set; }
        public int? TeamsTier { get; set; }
        public DateTimeOffset? PlanStart { get; set; }
        public DateTimeOffset? PlanEnd { get; set; }
        public int? DailyRemainingPercent { get; set; }
        public int? WeeklyRemainingPercent { get; set; }
        public DateTimeOffset? DailyResetAt { get; set; }
        public DateTimeOffset? WeeklyResetAt { get; set; }
        public int? GracePeriodStatus { get; set; }
    }

    private sealed class ProtobufReader(byte[] bytes)
    {
        private readonly byte[] _bytes = bytes;
        private int _offset;

        public bool TryReadField(out int field, out int wireType)
        {
            if (_offset >= _bytes.Length)
            {
                field = 0;
                wireType = 0;
                return false;
            }

            var key = ReadVarint();
            field = checked((int)(key >> 3));
            wireType = checked((int)(key & 7));
            if (field <= 0) throw new UsageProviderRequestException("Windsurf protobuf содержит некорректный номер поля.");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            var shift = 0;
            while (_offset < _bytes.Length && shift < 64)
            {
                var current = _bytes[_offset++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0) return value;
                shift += 7;
            }

            throw new UsageProviderRequestException("Windsurf protobuf оборван.");
        }

        public byte[] ReadBytes()
        {
            var length = checked((int)ReadVarint());
            if (length < 0 || _offset + length > _bytes.Length)
            {
                throw new UsageProviderRequestException("Windsurf protobuf содержит некорректную длину.");
            }

            var result = _bytes[_offset..(_offset + length)];
            _offset += length;
            return result;
        }

        public string ReadString()
            => Encoding.UTF8.GetString(ReadBytes());

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0: _ = ReadVarint(); break;
                case 1: Advance(8); break;
                case 2: _ = ReadBytes(); break;
                case 5: Advance(4); break;
                default: throw new UsageProviderRequestException($"Windsurf protobuf wire type {wireType} не поддержан.");
            }
        }

        private void Advance(int count)
        {
            if (_offset + count > _bytes.Length) throw new UsageProviderRequestException("Windsurf protobuf оборван.");
            _offset += count;
        }
    }

    private async Task<UsageSnapshot> GetDeepgramSnapshotAsync(CancellationToken cancellationToken)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential.ApiKey))
        {
            throw new UsageProviderConfigurationException("Для Deepgram не задан API-ключ.");
        }

        var baseValue = _configuration.GetValue(Descriptor.Id, "baseUrl") ?? "https://api.deepgram.com/v1";
        var baseUri = ParseBaseUrl(baseValue, "Deepgram");

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
                throw CreateHttpFailure($"Не удалось открыть консоль {Descriptor.DisplayName}", pageResponse);
            }

            var page = await ReadBoundedResponseBodyAsync(pageResponse.Content, cancellationToken).ConfigureAwait(false);
            secToken = ExtractSecToken(page);
        }

        if (string.IsNullOrWhiteSpace(secToken))
        {
            throw new UsageProviderConfigurationException(
                $"В Cookie/консоли {Descriptor.DisplayName} не найден sec_token. Обновите Cookie после входа в консоль.");
        }

        var action = isQwen ? "IntlBroadScopeAspnGateway" : "IntlBroadScopeAspnGateway";
        var apiName = "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage";
        var parameters = SerializeAlibabaParameters(apiName, dashboardUrl, isQwen);
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
            throw CreateHttpFailure($"{Descriptor.DisplayName} gateway", response);
        }

        var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
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
            throw CreateHttpFailure("T3 Chat", response);
        }

        var body = await ReadBoundedResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
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
            throw CreateHttpFailure("Deepgram", response);
        }

        var content = await ReadBoundedResponseBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
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
        if (Descriptor.Id.Equals("alibaba", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("ALIBABA_CODING_PLAN_API_KEY")
                ?? Environment.GetEnvironmentVariable("ALIBABA_QWEN_API_KEY")
                ?? Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
        }
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

    private async Task<UsageSnapshot> ExecuteWithDeadlineAsync(
        Func<CancellationToken, Task<UsageSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCts.CancelAfter(_requestTimeout);
        try
        {
            return await operation(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
        {
            throw new UsageProviderRequestException(
                $"{Descriptor.DisplayName}: request timed out after {_requestTimeout.TotalSeconds:0.#} seconds.",
                failureKind: UsageProviderFailureKind.Timeout);
        }
    }

    private async Task<string> ReadBoundedResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedResponseBytesAsync(content, cancellationToken).ConfigureAwait(false);
        return GetContentEncoding(content).GetString(bytes);
    }

    private async Task<byte[]> ReadBoundedResponseBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > _maxResponseBodyBytes)
        {
            throw new UsageProviderRequestException(
                $"Response exceeds the maximum response size of {_maxResponseBodyBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (bytes.Length > _maxResponseBodyBytes - read)
                {
                    throw new UsageProviderRequestException(
                        $"Response exceeds the maximum response size of {_maxResponseBodyBytes} bytes.");
                }

                await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return bytes.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Encoding GetContentEncoding(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Fall back to UTF-8 for invalid or unsupported response declarations.
            }
        }

        return Encoding.UTF8;
    }

    private sealed record ResolvedCredential(string? ApiKey, string? CookieHeader);
}
