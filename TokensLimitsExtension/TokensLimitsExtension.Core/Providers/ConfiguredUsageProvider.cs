using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
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

        if (_descriptor.Id.Equals("jetbrains", StringComparison.OrdinalIgnoreCase))
        {
            return await GetJetBrainsSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("kiro", StringComparison.OrdinalIgnoreCase))
        {
            return await GetKiroSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return await GetOllamaSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("opencode", StringComparison.OrdinalIgnoreCase)
            || (_descriptor.Id.Equals("opencodego", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ResolveCredential().ApiKey)))
        {
            return await GetOpenCodeSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("minimax", StringComparison.OrdinalIgnoreCase))
        {
            var miniMaxCredential = ResolveCredential();
            if (string.IsNullOrWhiteSpace(miniMaxCredential.ApiKey)
                && !string.IsNullOrWhiteSpace(miniMaxCredential.CookieHeader))
            {
                return await GetMiniMaxWebSnapshotAsync(miniMaxCredential, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_descriptor.Id.Equals("kilo", StringComparison.OrdinalIgnoreCase))
        {
            return await GetKiloSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.AuthKind == UsageProviderAuthKind.Local)
        {
            return await GetLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("zed", StringComparison.OrdinalIgnoreCase))
        {
            return await GetZedSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return await GetOpenAiSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("amp", StringComparison.OrdinalIgnoreCase))
        {
            return await GetAmpSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.Id.Equals("windsurf", StringComparison.OrdinalIgnoreCase))
        {
            return await GetWindsurfSnapshotAsync(cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
            request.Headers.TryAddWithoutValidation("User-Agent", "TokensLimitsExtension/0.0.2");
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
            throw new UsageProviderRequestException(
                $"OpenCode server function: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            throw new UsageProviderRequestException(
                $"OpenCode page: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            throw new UsageProviderRequestException(
                $"MiniMax web: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            throw new UsageProviderRequestException(
                $"Kilo tRPC: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
                throw new UsageProviderRequestException(
                    $"Ollama API: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                throw new UsageProviderRequestException(
                    $"Локальный Ollama API: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task<UsageSnapshot> GetKiroSnapshotAsync(CancellationToken cancellationToken)
    {
        var configuredPath = _configuration.GetValue(Descriptor.Id, "cliPath");
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? "kiro-cli.exe" : configuredPath;
        var result = await RunProcessAsync(
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
        return ParseKiroUsage(output, DateTimeOffset.UtcNow);
    }

    private static UsageSnapshot ParseKiroUsage(string output, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new UsageProviderRequestException("Kiro: kiro-cli не вернул отчёт об использовании.");
        }

        var normalized = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", string.Empty);
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

        var resetAt = TryParseKiroReset(normalized, fetchedAt);
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

    private static DateTimeOffset? TryParseKiroReset(string text, DateTimeOffset now)
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

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new UsageProviderConfigurationException($"Не удалось запустить {fileName}.");
            }
        }
        catch (Win32Exception)
        {
            throw new UsageProviderConfigurationException(
                $"Не найден {fileName}. Установите Kiro CLI или укажите путь к нему в настройках.");
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, standardOutput.Result, standardError.Result);
    }

    private static double ParseFlexibleNumber(string raw)
        => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static int ParseInteger(string raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
            throw new UsageProviderRequestException(
                $"Zed profile: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            throw new UsageProviderRequestException(
                $"Amp {endpoint.Name}: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

        var snapshot = ParseAmpDisplayText(displayText, DateTimeOffset.UtcNow, endpoint.Name);
        _logger($"[TokensLimits] Provider {Descriptor.Id}: snapshot fetched from {endpoint.Name}.");
        return snapshot;
    }

    private UsageSnapshot ParseAmpDisplayText(string text, DateTimeOffset now, string source)
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
            metrics.Add(new UsageMetric($"Workspace {workspace.Groups[1].Value.Trim()}", FormatNumber(ParseInvariantNumber(workspace.Groups[2].Value)), "USD", Remaining: ParseInvariantNumber(workspace.Groups[2].Value)));
        }

        if (metrics.Count == 0)
        {
            throw new UsageProviderRequestException("Ответ Amp не содержит распознаваемых данных usage.");
        }

        return new UsageSnapshot(Descriptor.Id, Descriptor.DisplayName, primary, secondary, null, false)
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
            throw new UsageProviderRequestException(
                $"Windsurf GetPlanStatus: HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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
