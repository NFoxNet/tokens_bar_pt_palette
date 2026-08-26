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
    string? RequestBody = null);

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
        string? body = null)
        => new(name, url, "GET", header, prefix, apiKey, cookie, requiresBaseUrl, baseUrl, body);

    public static IReadOnlyDictionary<string, IReadOnlyList<UsageProviderEndpoint>> All { get; } =
        new Dictionary<string, IReadOnlyList<UsageProviderEndpoint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["abacus"] = [Get("compute-points", "https://apps.abacus.ai/api/_getOrganizationComputePoints", cookie: true, apiKey: false)],
            ["aiand"] = [Get("logs", "https://api.aiand.com/logs", apiKey: true)],
            ["alibaba"] = [new UsageProviderEndpoint("usage", "https://modelstudio.console.alibabacloud.com/data/api.json?action=zeldaEasy.broadscope-bailian.codingPlan.queryCodingPlanInstanceInfoV2&product=broadscope-bailian&api=queryCodingPlanInstanceInfoV2&currentRegionId=ap-southeast-1", "POST", "x-api-key", "", RequiresApiKey: true, UseConfiguredBaseUrl: true, RequestBody: "{}")],
            ["alibabatokenplan"] = [Get("usage", "https://bailian.console.aliyun.com/", cookie: true, apiKey: false)],
            ["amp"] =
            [
                new UsageProviderEndpoint("balance-api", "https://ampcode.com/api/internal?userDisplayBalanceInfo", "POST", "Authorization", "Bearer ", RequiresApiKey: true, RequestBody: "{\"method\":\"userDisplayBalanceInfo\"}"),
                Get("balance-web", "https://ampcode.com/api/internal?userDisplayBalanceInfo", cookie: true, apiKey: false),
            ],
            ["antigravity"] = [Get("quota", "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota", apiKey: true)],
            ["augment"] = [Get("subscription", "https://app.augmentcode.com/account/subscription", cookie: true, apiKey: false)],
            ["azureopenai"] = [Get("usage", "/openai/usage", header: "api-key", prefix: "", baseUrl: true)],
            ["bedrock"] = [Get("usage", "/usage", header: "Authorization", prefix: "Bearer ", baseUrl: true)],
            ["chutes"] = [Get("subscription-usage", "https://api.chutes.ai/users/me/subscription_usage", baseUrl: true)],
            ["claude"] = [Get("organization-usage", "https://api.anthropic.com/v1/organizations/usage_report/messages", header: "x-api-key", prefix: "")],
            ["clawrouter"] = [Get("usage", "https://clawrouter.openclaw.ai/api/usage", baseUrl: true)],
            ["clinepass"] = [Get("usage", "https://app.cline.bot/api/usage", cookie: true, apiKey: false, baseUrl: true)],
            ["codebuff"] = [new UsageProviderEndpoint("usage", "https://www.codebuff.com/api/v1/usage", "POST", "Authorization", "Bearer ", RequiresApiKey: true, UseConfiguredBaseUrl: true, RequestBody: "{\"fingerprintId\":\"tokens-limits\"}")],
            ["commandcode"] = [Get("credits", "https://api.commandcode.ai/internal/billing/credits", cookie: true, apiKey: false)],
            ["copilot"] = [Get("budget", "https://github.com/settings/billing/budgets", cookie: true, apiKey: false)],
            ["crof"] = [Get("usage", "https://crof.ai/api/usage", baseUrl: true)],
            ["cursor"] = [Get("usage-summary", "https://cursor.com/api/usage-summary", cookie: true, apiKey: false)],
            ["deepgram"] = [Get("projects", "https://api.deepgram.com/v1/projects")],
            ["deepinfra"] =
            [
                Get("checklist", "https://api.deepinfra.com/payment/checklist?compute_owed=true"),
                Get("usage", "https://api.deepinfra.com/payment/usage?from=current"),
            ],
            ["deepseek"] = [Get("balance", "https://api.deepseek.com/user/balance", baseUrl: true)],
            ["devin"] = [Get("usage", "https://app.devin.ai/api/usage", cookie: true, apiKey: false)],
            ["doubao"] = [Get("coding-plan", "https://open.volcengineapi.com/?Action=GetCodingPlanUsage&Version=2024-01-01", header: "Authorization", prefix: "Bearer ")],
            ["elevenlabs"] = [Get("subscription", "https://api.elevenlabs.io/v1/user/subscription", header: "xi-api-key", prefix: "", baseUrl: true)],
            ["factory"] = [Get("usage", "https://api.factory.ai/api/usage", cookie: true, apiKey: false)],
            ["fireworks"] = [Get("billing", "/v1/accounts/{accountId}/billing/summary", baseUrl: true)],
            ["gemini"] = [Get("quota", "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")],
            ["grok"] = [Get("settings", "https://cli-chat-proxy.grok.com/v1/settings", cookie: true, apiKey: false)],
            ["groq"] = [Get("usage", "https://api.groq.com/openai/v1/usage", baseUrl: true)],
            ["ibmbob"] = [Get("profile", "https://api.us-east.bob.ibm.com/admin/v1/profile", baseUrl: true)],
            ["jetbrains"] = [Get("local", null, apiKey: false)],
            ["kilo"] = [Get("organizations", "https://app.kilo.ai/api/trpc/user.getOrganizations", cookie: true, apiKey: false, baseUrl: true)],
            ["kimi"] =
            [
                Get("web-usage", "https://www.kimi.com/apiv2/kimi.gateway.billing.v1.BillingService/GetUsages", cookie: true, apiKey: false),
                Get("api-usage", "https://api.kimi.com/v1/usages", baseUrl: true),
            ],
            ["kiro"] = [Get("local", null, apiKey: false)],
            ["litellm"] = [Get("key-info", "/key/info", baseUrl: true, requiresBaseUrl: true)],
            ["llmproxy"] = [Get("usage", "/usage", baseUrl: true, requiresBaseUrl: true)],
            ["longcat"] = [Get("token-usage", "https://longcat.chat/api/lc-platform/v1/tokenUsage", cookie: true, apiKey: false)],
            ["manus"] = [Get("credits", "https://api.manus.im/user.v1.UserService/GetAvailableCredits", cookie: true, apiKey: false)],
            ["mimo"] = [Get("balance", "https://platform.xiaomimimo.com/api/v1/balance", cookie: true, apiKey: false)],
            ["minimax"] = [Get("coding-plan", "https://api.minimax.io/v1/api/openplatform/coding_plan/remains", baseUrl: true)],
            ["mistral"] = [Get("usage", "https://admin.mistral.ai/api/billing/v2/usage", cookie: true, apiKey: false)],
            ["moonshot"] = [Get("balance", "https://api.moonshot.ai/v1/users/me/balance", baseUrl: true)],
            ["neuralwatt"] = [Get("usage", "https://api.neuralwatt.com/v1/usage", baseUrl: true)],
            ["notion"] = [Get("credit-rate-limit", "https://app.notion.com/api/v3/getCreditRateLimitStatus", cookie: true, apiKey: false)],
            ["ollama"] = [Get("models", "http://127.0.0.1:11434/api/tags", apiKey: false)],
            ["openai"] =
            [
                Get("costs", "https://api.openai.com/v1/organization/costs", baseUrl: true),
                Get("usage", "https://api.openai.com/v1/organization/usage/completions", baseUrl: true),
            ],
            ["opencode"] = [Get("billing", "https://opencode.ai/_server/usage", cookie: true, apiKey: false)],
            ["opencodego"] = [Get("usage", "https://opencode.ai/zen/go/v1/usage", cookie: true, apiKey: false)],
            ["openrouter"] = [Get("key", "https://openrouter.ai/api/v1/key", baseUrl: true)],
            ["perplexity"] = [Get("credits", "https://www.perplexity.ai/rest/billing/credits?version=2.18&source=default", cookie: true, apiKey: false)],
            ["poe"] = [Get("usage", "https://poe.com/api/account/usage", baseUrl: true)],
            ["qoder"] = [Get("credits", "https://qoder.com/api/v2/me/usages/big_model_credits", cookie: true, apiKey: false)],
            ["qwencloud"] = [Get("usage", "https://bailian.console.aliyun.com/", cookie: true, apiKey: false)],
            ["sakana"] = [Get("billing", "https://console.sakana.ai/billing", cookie: true, apiKey: false)],
            ["stepfun"] = [Get("plan-status", "https://platform.stepfun.com/api/step.openapi.devcenter.Dashboard/GetStepPlanStatus", cookie: true, apiKey: false)],
            ["sub2api"] = [Get("usage", "/v1/usage", baseUrl: true, requiresBaseUrl: true)],
            ["synthetic"] = [Get("usage", "/v1/usage", baseUrl: true, requiresBaseUrl: true)],
            ["t3chat"] = [Get("customer", "https://t3.chat/api/trpc/getCustomerData", cookie: true, apiKey: false)],
            ["venice"] = [Get("usage", "https://api.venice.ai/api/v1/usage", baseUrl: true)],
            ["vertexai"] = [Get("quota", "https://monitoring.googleapis.com/v3/projects/{projectId}/timeSeries")],
            ["warp"] = [new UsageProviderEndpoint("request-limit", "https://app.warp.dev/graphql/v2?op=GetRequestLimitInfo", "POST", "Authorization", "Bearer ", RequiresApiKey: true, UseConfiguredBaseUrl: true)],
            ["wayfinder"] = [Get("usage", "/v1/usage", apiKey: false, baseUrl: true, requiresBaseUrl: true)],
            ["windsurf"] = [Get("plan-status", "https://windsurf.com/_backend/exa.seat_management_pb.SeatManagementService/GetPlanStatus", cookie: true, apiKey: false)],
            ["xai"] = [Get("usage", "https://api.x.ai/v1/usage", baseUrl: true)],
            ["zai"] = [Get("quota", "https://api.z.ai/api/monitor/usage/quota", baseUrl: true)],
            ["zed"] = [Get("profile", "https://cloud.zed.dev/client/users/me", apiKey: false)],
            ["zenmux"] = [Get("subscription", "https://zenmux.ai/api/v1/management/subscription/detail", baseUrl: true)],
            ["zoommate"] = [Get("credits", "https://ai.zoom.us/ai-computer/api/v1/credits/status", cookie: true, apiKey: false)],
        };

    public static IReadOnlyList<UsageProviderEndpoint> For(string providerId)
        => All.TryGetValue(providerId, out var endpoints) ? endpoints : [];
}
