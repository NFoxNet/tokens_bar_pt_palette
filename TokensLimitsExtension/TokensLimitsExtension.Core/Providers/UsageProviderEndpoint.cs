namespace TokensLimitsExtension.Core.Providers;

public sealed record UsageProviderEndpoint(
    string Name,
    string? Url,
    string HttpMethod = "GET",
    string? ApiKeyHeader = "Authorization",
    string ApiKeyPrefix = "Bearer ",
    bool RequiresApiKey = false,
    bool RequiresCookie = false,
    bool RequiresBaseUrl = false,
    bool UseConfiguredBaseUrl = false,
    string? RequestBody = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// Stable endpoint inventory for the providers from CodexBar's manifest.
/// Every entry points at a provider-owned API or dashboard endpoint. If a
/// provider changes its private endpoint, only this catalog and its parser need
/// updating; the UI and registry remain unchanged.
/// </summary>
public static class UsageProviderEndpointCatalog
{
    private static UsageProviderEndpoint Get(
        string name,
        string? url,
        string header = "Authorization",
        string prefix = "Bearer ",
        bool apiKey = true,
        bool cookie = false,
        bool baseUrl = false,
        bool requiresBaseUrl = false,
        string? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(name, url, "GET", header, prefix, apiKey, cookie, requiresBaseUrl, baseUrl, body, headers);

    public static IReadOnlyDictionary<string, IReadOnlyList<UsageProviderEndpoint>> All { get; } =
        new Dictionary<string, IReadOnlyList<UsageProviderEndpoint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["abacus"] =
            [
                Get("compute-points", "https://apps.abacus.ai/api/_getOrganizationComputePoints", cookie: true, apiKey: false),
                Get("billing-info", "https://apps.abacus.ai/api/_getBillingInfo", cookie: true, apiKey: false),
            ],
            ["aiand"] = [Get("logs", "https://api.aiand.com/logs", apiKey: true)],
            ["alibaba"] = [new UsageProviderEndpoint("usage", "https://modelstudio.console.alibabacloud.com/data/api.json?action=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2&product=broadscope-bailian&api=queryCodingPlanInstanceInfoV2&currentRegionId=ap-southeast-1", "POST", "x-api-key", "", RequiresApiKey: true, UseConfiguredBaseUrl: true, RequestBody: "{}")],
            ["alibabatokenplan"] = [Get("usage", "https://bailian.console.aliyun.com/", cookie: true, apiKey: false)],
            ["amp"] =
            [
                new UsageProviderEndpoint("balance-api", "https://ampcode.com/api/internal?userDisplayBalanceInfo", "POST", "Authorization", "Bearer ", RequiresApiKey: true, RequestBody: "{\"method\":\"userDisplayBalanceInfo\"}"),
                Get("balance-web", "https://ampcode.com/api/internal?userDisplayBalanceInfo", cookie: true, apiKey: false),
            ],
            ["antigravity"] = [Get("quota", "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota", apiKey: true)],
            ["augment"] =
            [
                Get("credits", "https://app.augmentcode.com/api/credits", cookie: true, apiKey: false),
                Get("subscription", "https://app.augmentcode.com/api/subscription", cookie: true, apiKey: false),
            ],
            ["azureopenai"] = [new UsageProviderEndpoint(
                "validation",
                "/openai/deployments/{deploymentName}/chat/completions?api-version={apiVersion}",
                "POST",
                "api-key",
                "",
                RequiresApiKey: true,
                UseConfiguredBaseUrl: true,
                RequestBody: "{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":1}")],
            ["bedrock"] = [Get("usage", "/usage", header: "Authorization", prefix: "Bearer ", baseUrl: true)],
            ["chutes"] = [Get("subscription-usage", "https://api.chutes.ai/users/me/subscription_usage", baseUrl: true)],
            ["claude"] =
            [
                Get("organization-usage", "https://api.anthropic.com/v1/organizations/usage_report/messages", header: "x-api-key", prefix: "", headers: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" }),
                Get("organization-cost", "https://api.anthropic.com/v1/organizations/cost_report", header: "x-api-key", prefix: "", headers: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" }),
                Get("web-usage", "https://claude.ai/api/organizations/{accountId}/usage", cookie: true, apiKey: false),
                Get("web-credits", "https://claude.ai/api/organizations/{accountId}/prepaid/credits", cookie: true, apiKey: false),
            ],
            ["clawrouter"] = [Get("usage", "https://clawrouter.openclaw.ai/v1/usage", baseUrl: true)],
            ["clinepass"] = [Get("usage", "https://api.cline.bot/api/v1/users/me/plan/usage-limits")],
            ["codebuff"] = [new UsageProviderEndpoint("usage", "https://www.codebuff.com/api/v1/usage", "POST", "Authorization", "Bearer ", RequiresApiKey: true, UseConfiguredBaseUrl: true, RequestBody: "{\"fingerprintId\":\"tokens-limits\"}")],
            ["commandcode"] = [Get("credits", "https://api.commandcode.ai/internal/billing/credits", cookie: true, apiKey: false)],
            ["copilot"] = [Get("budget", "https://github.com/settings/billing/budgets", cookie: true, apiKey: false)],
            ["crof"] = [Get("usage", "https://crof.ai/usage_api/", baseUrl: true)],
            ["cursor"] = [Get("usage-summary", "https://cursor.com/api/usage-summary", cookie: true, apiKey: false)],
            ["deepgram"] = [Get("projects", "https://api.deepgram.com/v1/projects", prefix: "Token ", baseUrl: true)],
            ["deepinfra"] =
            [
                Get("checklist", "https://api.deepinfra.com/payment/checklist?compute_owed=true"),
                Get("usage", "https://api.deepinfra.com/payment/usage?from=current"),
            ],
            ["deepseek"] =
            [
                Get("balance", "https://api.deepseek.com/user/balance", baseUrl: true),
                Get("dashboard-summary", "https://platform.deepseek.com/api/v0/users/get_user_summary", cookie: true, apiKey: false),
                Get("dashboard-amount", "https://platform.deepseek.com/api/v0/usage/amount", cookie: true, apiKey: false),
                Get("dashboard-cost", "https://platform.deepseek.com/api/v0/usage/cost", cookie: true, apiKey: false),
            ],
            ["devin"] = [Get("usage", "https://app.devin.ai/api/usage", cookie: true, apiKey: false)],
            ["doubao"] =
            [
                Get("coding-plan", "https://open.volcengineapi.com/?Action=GetCodingPlanUsage&Version=2024-01-01", header: "Authorization", prefix: "Bearer "),
                Get("afp-usage", "https://open.volcengineapi.com/?Action=GetAFPUsage&Version=2024-01-01", header: "Authorization", prefix: "Bearer "),
            ],
            ["elevenlabs"] = [Get("subscription", "https://api.elevenlabs.io/v1/user/subscription", header: "xi-api-key", prefix: "", baseUrl: true)],
            ["factory"] = [Get("usage", "https://api.factory.ai/api/usage", cookie: true, apiKey: false)],
            ["fireworks"] = [Get("billing", "/v1/accounts/{accountId}/billing/summary", baseUrl: true)],
            ["gemini"] = [Get("quota", "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")],
            ["grok"] =
            [
                Get("billing", "https://cli-chat-proxy.grok.com/v1/billing?format=credits", cookie: true, apiKey: false),
                Get("settings", "https://cli-chat-proxy.grok.com/v1/settings", cookie: true, apiKey: false),
                Get("web-credits", "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig", cookie: true, apiKey: false),
            ],
            ["groq"] = [Get("usage", "https://api.groq.com/openai/v1/usage", baseUrl: true)],
            ["ibmbob"] = [Get("profile", "https://api.us-east.bob.ibm.com/admin/v1/profile", baseUrl: true)],
            ["jetbrains"] = [Get("local", null, apiKey: false)],
            ["kilo"] =
            [
                Get("organizations", "https://app.kilo.ai/api/trpc/user.getOrganizations", cookie: true, apiKey: false, baseUrl: true),
                Get("profile", "https://api.kilo.ai/api/profile", cookie: true, apiKey: false),
            ],
            ["kimi"] =
            [
                Get("web-usage", "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages", cookie: true, apiKey: false),
                Get("web-subscription", "https://www.kimi.com/apiv2/kimi.gateway.membership.v2.MembershipService/GetSubscriptionStats", cookie: true, apiKey: false),
                Get("api-usage", "https://api.kimi.com/v1/usages", baseUrl: true),
            ],
            ["kiro"] = [Get("local", null, apiKey: false)],
            ["litellm"] = [Get("key-info", "/key/info", baseUrl: true, requiresBaseUrl: true)],
            ["llmproxy"] = [Get("usage", "/usage", baseUrl: true, requiresBaseUrl: true)],
            ["longcat"] = [Get("token-usage", "https://longcat.chat/api/lc-platform/v1/tokenUsage", cookie: true, apiKey: false)],
            ["manus"] = [Get("credits", "https://api.manus.im/user.v1.UserService/GetAvailableCredits", cookie: true, apiKey: false)],
            ["mimo"] =
            [
                Get("balance", "https://platform.xiaomimimo.com/api/v1/balance", cookie: true, apiKey: false),
                Get("account", "https://platform.xiaomimimo.com/api/v1/account", cookie: true, apiKey: false),
            ],
            ["minimax"] = [Get("coding-plan", "https://api.minimax.io/v1/api/openplatform/coding_plan/remains", baseUrl: true)],
            ["mistral"] =
            [
                Get("usage", "https://admin.mistral.ai/api/billing/v2/usage", cookie: true, apiKey: false),
                Get("vibe-usage", "https://console.mistral.ai/api-ui/trpc/billing.vibeUsage?batch=1&input=%7B%220%22%3A%7B%22json%22%3Anull%2C%22meta%22%3A%7B%22values%22%3A%5B%22undefined%22%5D%2C%22v%22%3A1%7D%7D%7D", cookie: true, apiKey: false),
            ],
            ["moonshot"] = [Get("balance", "https://api.moonshot.ai/v1/users/me/balance", baseUrl: true)],
            ["neuralwatt"] = [Get("usage", "https://api.neuralwatt.com/v1/usage", baseUrl: true)],
            ["notion"] = [Get("credit-rate-limit", "https://app.notion.com/api/v3/getCreditRateLimitStatus", cookie: true, apiKey: false)],
            ["ollama"] = [Get("models", "http://127.0.0.1:11434/api/tags", apiKey: false)],
            ["openai"] =
            [
                Get("credit-grants", "https://api.openai.com/v1/dashboard/billing/credit_grants", baseUrl: true),
                Get("costs", "https://api.openai.com/v1/organization/costs", baseUrl: true),
                Get("usage", "https://api.openai.com/v1/organization/usage/completions", baseUrl: true),
            ],
            ["opencode"] =
            [
                Get("billing", "https://opencode.ai/_server/usage", cookie: true, apiKey: false),
                Get("workspace-usage", "https://opencode.ai/workspace/{accountId}/usage", cookie: true, apiKey: false),
            ],
            ["opencodego"] = [Get("usage", "https://opencode.ai/zen/go/v1/usage", cookie: true, apiKey: false)],
            ["openrouter"] = [Get("key", "https://openrouter.ai/api/v1/key", baseUrl: true)],
            ["perplexity"] = [Get("credits", "https://www.perplexity.ai/rest/billing/credits?version=2.18&source=default", cookie: true, apiKey: false)],
            ["poe"] =
            [
                Get("current-balance", "https://api.poe.com/usage/current_balance", baseUrl: true),
                Get("points-history", "https://api.poe.com/usage/points_history?limit=100", baseUrl: true),
            ],
            ["qoder"] =
            [
                Get("credits", "https://qoder.com/api/v2/me/usages/big_model_credits", cookie: true, apiKey: false,
                    headers: new Dictionary<string, string>
                    {
                        ["X-Requested-With"] = "XMLHttpRequest",
                        ["Bx-V"] = "2.5.35",
                    }),
                Get("credits-cn", "https://qoder.com.cn/api/v2/me/usages/big_model_credits", cookie: true, apiKey: false,
                    headers: new Dictionary<string, string>
                    {
                        ["X-Requested-With"] = "XMLHttpRequest",
                        ["Bx-V"] = "2.5.35",
                    }),
            ],
            ["qwencloud"] =
            [
                Get("token-plan", "https://home.qwencloud.com/billing/subscription/token-plan-individual", cookie: true, apiKey: false),
                Get("data-token-plan", "https://cs-data.qwencloud.com/billing/subscription/token-plan-individual", cookie: true, apiKey: false),
            ],
            ["sakana"] = [Get("billing", "https://console.sakana.ai/billing", cookie: true, apiKey: false)],
            ["stepfun"] =
            [
                Get("rate-limit", "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/QueryStepPlanRateLimit", cookie: true, apiKey: false),
                Get("plan-status", "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus", cookie: true, apiKey: false),
            ],
            ["sub2api"] = [Get("usage", "/v1/usage", baseUrl: true, requiresBaseUrl: true)],
            ["synthetic"] = [Get("usage", "https://api.synthetic.new/v2/quotas", baseUrl: true)],
            ["t3chat"] = [Get(
                "customer",
                "https://t3.chat/api/trpc/getCustomerData?batch=1&input=%7B%220%22%3A%7B%22json%22%3A%7B%22sessionId%22%3Anull%7D%2C%22meta%22%3A%7B%22values%22%3A%7B%22sessionId%22%3A%5B%22undefined%22%5D%7D%7D%7D%7D",
                cookie: true,
                apiKey: false,
                headers: new Dictionary<string, string>
                {
                    ["Origin"] = "https://t3.chat",
                    ["Referer"] = "https://t3.chat/settings/customization",
                    ["trpc-accept"] = "application/jsonl",
                    ["x-trpc-source"] = "web-client",
                    ["x-trpc-batch"] = "true",
                })],
            ["venice"] = [Get("balance", "https://api.venice.ai/api/v1/billing/balance", baseUrl: true)],
            ["vertexai"] = [Get("quota", "https://monitoring.googleapis.com/v3/projects/{projectId}/timeSeries")],
            ["warp"] = [new UsageProviderEndpoint(
                "request-limit",
                "https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo",
                "POST",
                "Authorization",
                "Bearer ",
                RequiresApiKey: true,
                UseConfiguredBaseUrl: true,
                RequestBody: "{\"query\":\"query GetRequestLimitInfo($requestContext: RequestContext!) { user(requestContext: $requestContext) { __typename ... on UserOutput { user { requestLimitInfo { isUnlimited nextRefreshTime requestLimit requestsUsedSinceLastRefresh } bonusGrants { requestCreditsGranted requestCreditsRemaining expiration } workspaces { bonusGrantsInfo { grants { requestCreditsGranted requestCreditsRemaining expiration } } } } } } }\",\"variables\":{\"requestContext\":{\"clientContext\":{},\"osContext\":{\"category\":\"Windows\",\"name\":\"Windows\",\"version\":\"11\"}}},\"operationName\":\"GetRequestLimitInfo\"}",
                Headers: new Dictionary<string, string>
                {
                    ["x-warp-client-id"] = "warp-app",
                    ["x-warp-os-category"] = "Windows",
                    ["x-warp-os-name"] = "Windows",
                    ["User-Agent"] = "Warp/1.0",
                })],
            ["wayfinder"] = [Get("usage", "/v1/usage", apiKey: false, baseUrl: true, requiresBaseUrl: true)],
            ["windsurf"] = [Get("plan-status", "https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus", cookie: true, apiKey: false)],
            ["xai"] =
            [
                Get("prepaid-balance", "https://management-api.x.ai/v1/billing/teams/{accountId}/prepaid/balance", baseUrl: true),
                new UsageProviderEndpoint(
                    "usage",
                    "https://management-api.x.ai/v1/billing/teams/{accountId}/usage",
                    "POST",
                    "Authorization",
                    "Bearer ",
                    RequiresApiKey: true,
                    UseConfiguredBaseUrl: true,
                    RequestBody: "{\"analyticsRequest\":{\"timeRange\":{\"startTime\":\"{startTime}\",\"endTime\":\"{endTime}\",\"timezone\":\"Etc/GMT\"},\"timeUnit\":\"TIME_UNIT_DAY\",\"values\":[{\"name\":\"usd\",\"aggregation\":\"AGGREGATION_SUM\"}],\"groupBy\":[],\"filters\":[]}}")
            ],
            ["zai"] = [Get("quota", "https://api.z.ai/api/monitor/usage/quota", baseUrl: true)],
            ["zed"] = [Get("profile", "https://cloud.zed.dev/client/users/me", apiKey: false)],
            ["zenmux"] = [Get("subscription", "https://zenmux.ai/api/v1/management/subscription/detail", baseUrl: true)],
            ["zoommate"] = [Get("credits", "https://ai.zoom.us/ai-computer/api/v1/credits/status", cookie: true, apiKey: false)],
        };

    public static IReadOnlyList<UsageProviderEndpoint> For(string providerId)
        => All.TryGetValue(providerId, out var endpoints) ? endpoints : [];
}
