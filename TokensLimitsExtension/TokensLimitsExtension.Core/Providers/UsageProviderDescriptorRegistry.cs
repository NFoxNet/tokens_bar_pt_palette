namespace TokensLimitsExtension.Core.Providers;

/// <summary>
/// Ordered manifest of supported provider descriptors.
/// Add a descriptor here when a new provider is introduced; the runtime registry
/// decides which instantiated providers are available in the current process.
/// </summary>
public static class UsageProviderDescriptorRegistry
{
    private static UsageProviderSettingDescriptor ApiKey(string environmentVariable, string label = "API-ключ")
        => new("apiKey", label, $"Ключ провайдера. Также можно задать переменную {environmentVariable}.", true, environmentVariable);

    private static UsageProviderSettingDescriptor ApiKeyField(
        string key,
        string environmentVariable,
        string label)
        => new(key, label, $"Ключ провайдера. Также можно задать переменную {environmentVariable}.", true, environmentVariable);

    private static UsageProviderSettingDescriptor Cookie()
        => new("cookieHeader", "Cookie-заголовок", "Необязательный Cookie-заголовок для личного веб-кабинета.", true);

    private static UsageProviderSettingDescriptor BaseUrl(string environmentVariable = "")
        => new("baseUrl", "Базовый URL", string.IsNullOrWhiteSpace(environmentVariable)
            ? "Переопределение API-адреса; оставьте пустым для официального адреса."
            : $"Переопределение API-адреса. Также можно задать переменную {environmentVariable}.", false, string.IsNullOrWhiteSpace(environmentVariable) ? null : environmentVariable);

    private static UsageProviderSettingDescriptor Account(string label = "Аккаунт / организация")
        => new("accountId", label, "Необязательный идентификатор аккаунта, организации или команды.");

    private static UsageProviderSettingDescriptor Project()
        => new("projectId", "Проект", "Необязательный идентификатор проекта или deployment.");

    private static UsageProviderSettingDescriptor Region()
        => new("region", "Регион", "Регион API, если провайдер его требует.", false, null, "us-east-1");

    private static UsageProviderSettingDescriptor Plain(
        string key,
        string label,
        string description,
        string? environmentVariable = null,
        string? defaultValue = null)
        => new(key, label, description, false, environmentVariable, defaultValue);

    public static UsageProviderDescriptor Codex { get; } = new(
        "codex",
        "Codex",
        UsageProviderAuthKind.OAuth,
        "https://chatgpt.com/codex/settings/usage",
        defaultEnabled: true,
        sourceDescription: "Официальный Codex API с локальным журналом сессий как штатным fallback.");

    private static UsageProviderDescriptor Api(
        string id,
        string displayName,
        string dashboardUrl,
        string environmentVariable,
        bool withBaseUrl = false,
        bool withAccount = false,
        bool withProject = false,
        bool withRegion = false)
    {
        var settings = new List<UsageProviderSettingDescriptor> { ApiKey(environmentVariable) };
        if (withBaseUrl) settings.Add(BaseUrl());
        if (withAccount) settings.Add(Account());
        if (withProject) settings.Add(Project());
        if (withRegion) settings.Add(Region());
        return new UsageProviderDescriptor(
            id,
            displayName,
            UsageProviderAuthKind.ApiKey,
            dashboardUrl,
            settings: settings,
            sourceDescription: "Официальный API провайдера; отображаются только значения, подтверждённые ответом API.");
    }

    private static UsageProviderDescriptor CookieProvider(
        string id,
        string displayName,
        string dashboardUrl,
        bool withBaseUrl = false)
    {
        var settings = new List<UsageProviderSettingDescriptor> { Cookie() };
        if (withBaseUrl) settings.Add(BaseUrl());
        return new UsageProviderDescriptor(
            id,
            displayName,
            UsageProviderAuthKind.Cookie,
            dashboardUrl,
            settings: settings,
            sourceDescription: "Веб-кабинет провайдера через настроенный Cookie; без Cookie данные не подменяются.");
    }

    private static UsageProviderDescriptor Local(
        string id,
        string displayName,
        string? dashboardUrl = null)
        => new(
            id,
            displayName,
            UsageProviderAuthKind.Local,
            dashboardUrl,
            settings: [new("dataPath", "Путь к данным", "Необязательный путь к локальному файлу или каталогу провайдера.")],
            sourceDescription: "Локальный источник провайдера; сетевые лимиты не придумываются.");

    public static IReadOnlyList<UsageProviderDescriptor> All { get; } =
    [
        Codex,
        CookieProvider("abacus", "Abacus AI", "https://apps.abacus.ai/chatllm/admin/compute-points-usage"),
        Api("aiand", "ai&", "https://console.aiand.com", "AIAND_API_KEY"),
        new(
            "alibaba",
            "Alibaba Coding Plan",
            UsageProviderAuthKind.ApiKey,
            "https://modelstudio.console.alibabacloud.com",
            settings:
            [
                ApiKey("DASHSCOPE_API_KEY"),
                Plain("region", "API-регион", "intl для международного Model Studio или cn для материкового Китая.", "ALIBABA_CODING_PLAN_REGION", "intl"),
                BaseUrl(),
            ],
            sourceDescription: "Официальный Alibaba Coding Plan API; ключ передаётся в Authorization, x-api-key и X-DashScope-API-Key."),
        CookieProvider("alibabatokenplan", "Alibaba Token Plan", "https://bailian.console.aliyun.com"),
        new("amp", "Amp", UsageProviderAuthKind.ApiKeyOrCookie, "https://ampcode.com/settings/usage", settings: [ApiKey("AMP_API_KEY"), Cookie(), BaseUrl()]),
        new("antigravity", "Antigravity", UsageProviderAuthKind.OAuth, "https://antigravity.google", settings: [new("credentialsJson", "OAuth credentials JSON", "OAuth credentials для Antigravity.", true)]),
        CookieProvider("augment", "Augment", "https://app.augmentcode.com/account/subscription"),
        new(
            "azureopenai",
            "Azure OpenAI",
            UsageProviderAuthKind.ApiKey,
            "https://ai.azure.com",
            settings:
            [
                ApiKey("AZURE_OPENAI_API_KEY"),
                BaseUrl("AZURE_OPENAI_ENDPOINT"),
                Plain("deploymentName", "Deployment", "Имя deployment Azure OpenAI.", "AZURE_OPENAI_DEPLOYMENT"),
                Plain("apiVersion", "Версия API", "Версия Azure OpenAI API.", "AZURE_OPENAI_API_VERSION", "2024-10-21"),
            ],
            sourceDescription: "Проверка доступности указанного Azure OpenAI deployment через официальный chat completions API; Azure не публикует общий процент квоты этим endpoint.") ,
        Api("bedrock", "AWS Bedrock", "https://console.aws.amazon.com/bedrock", "AWS_ACCESS_KEY_ID", withBaseUrl: true, withRegion: true),
        Api("chutes", "Chutes", "https://chutes.ai", "CHUTES_API_KEY", withBaseUrl: true),
        new("claude", "Claude", UsageProviderAuthKind.OAuthOrCookie, "https://claude.ai/settings/usage", settings: [ApiKey("ANTHROPIC_API_KEY", "Admin API-ключ"), Cookie(), Account("Организация")]),
        Api("clawrouter", "ClawRouter", "https://clawrouter.openclaw.ai/dashboard/access", "CLAWROUTER_API_KEY", withBaseUrl: true),
        Api("clinepass", "ClinePass", "https://app.cline.bot/dashboard/subscription", "CLINE_API_KEY"),
        Api("codebuff", "Codebuff", "https://www.codebuff.com/usage", "CODEBUFF_API_KEY", withBaseUrl: true),
        CookieProvider("commandcode", "Command Code", "https://commandcode.ai/studio"),
        new("copilot", "Copilot", UsageProviderAuthKind.OAuthOrCookie, "https://github.com/settings/copilot", settings: [new("oauthToken", "GitHub OAuth token", "Токен GitHub Copilot, если автоматический вход недоступен.", true), Cookie()]),
        Api("crof", "Crof", "https://crof.ai/dashboard", "CROF_API_KEY", withBaseUrl: true),
        CookieProvider("cursor", "Cursor", "https://cursor.com/dashboard?tab=usage"),
        Api("deepgram", "Deepgram", "https://console.deepgram.com/project/", "DEEPGRAM_API_KEY", withBaseUrl: true, withProject: true),
        Api("deepinfra", "DeepInfra", "https://deepinfra.com/dash", "DEEPINFRA_API_KEY", withBaseUrl: true),
        Api("deepseek", "DeepSeek", "https://platform.deepseek.com/usage", "DEEPSEEK_API_KEY", withBaseUrl: true),
        CookieProvider("devin", "Devin", "https://app.devin.ai/settings/usage"),
        Api("doubao", "Doubao", "https://console.volcengine.com/ark", "ARK_API_KEY", withBaseUrl: true, withRegion: true),
        Api("elevenlabs", "ElevenLabs", "https://elevenlabs.io/app/developers/usage", "ELEVENLABS_API_KEY", withBaseUrl: true),
        CookieProvider("factory", "Droid", "https://app.factory.ai/settings/billing"),
        Api("fireworks", "Fireworks", "https://app.fireworks.ai", "FIREWORKS_API_KEY", withBaseUrl: true, withAccount: true),
        new("gemini", "Gemini", UsageProviderAuthKind.OAuth, "https://gemini.google.com", settings: [new("oauthToken", "Google OAuth token", "OAuth access token Gemini CLI.", true), Project()]),
        CookieProvider("grok", "Grok", "https://grok.com/?_s=usage"),
        Api("groq", "Groq", "https://console.groq.com/dashboard/usage", "GROQ_API_KEY", withBaseUrl: true),
        Api("ibmbob", "IBM Bob", "https://bob.ibm.com", "IBM_BOB_API_KEY", withBaseUrl: true),
        Local("jetbrains", "JetBrains AI"),
        Api("kilo", "Kilo", "https://app.kilo.ai/usage", "KILO_API_KEY", withBaseUrl: true),
        new("kimi", "Kimi Code", UsageProviderAuthKind.ApiKeyOrCookie, "https://www.kimi.com/code/console", settings: [ApiKey("KIMI_API_KEY"), Cookie(), BaseUrl()]),
        Local("kiro", "Kiro", "https://app.kiro.dev/account/usage"),
        Api("litellm", "LiteLLM", "", "LITELLM_API_KEY", withBaseUrl: true),
        Api("llmproxy", "LLM Proxy", "", "LLMPROXY_API_KEY", withBaseUrl: true),
        CookieProvider("longcat", "LongCat", "https://longcat.chat/platform/"),
        CookieProvider("manus", "Manus", "https://manus.im"),
        CookieProvider("mimo", "Xiaomi MiMo", "https://platform.xiaomimimo.com/#/console/balance"),
        CookieProvider("minimax", "MiniMax", "https://platform.minimax.io/user-center/payment/coding-plan", withBaseUrl: true),
        CookieProvider("mistral", "Mistral", "https://admin.mistral.ai/organization/usage"),
        Api("moonshot", "Moonshot", "https://platform.moonshot.ai/console/account", "MOONSHOT_API_KEY", withBaseUrl: true),
        Api("neuralwatt", "Neuralwatt", "https://portal.neuralwatt.com/dashboard", "NEURALWATT_API_KEY", withBaseUrl: true),
        CookieProvider("notion", "Notion AI", "https://app.notion.com/"),
        Local("ollama", "Ollama", "https://ollama.com/settings"),
        new(
            "openai",
            "OpenAI",
            UsageProviderAuthKind.ApiKey,
            "https://platform.openai.com/usage",
            settings:
            [
                ApiKey("OPENAI_API_KEY"),
                ApiKeyField("adminApiKey", "OPENAI_ADMIN_KEY", "Admin API-ключ"),
                BaseUrl(),
                Account(),
                Project(),
                Plain("historyDays", "Дней истории", "Сколько последних дней запрашивать в Usage API OpenAI (1–365).", "OPENAI_HISTORY_DAYS", "30"),
            ],
            sourceDescription: "Официальные OpenAI Usage/Costs API; для организации обычно требуется Admin API-ключ.") ,
        CookieProvider("opencode", "OpenCode", "https://opencode.ai/auth"),
        CookieProvider("opencodego", "OpenCode Go", "https://opencode.ai/auth"),
        new(
            "openrouter",
            "OpenRouter",
            UsageProviderAuthKind.ApiKey,
            "https://openrouter.ai/settings/credits",
            settings:
            [
                ApiKey("OPENROUTER_API_KEY"),
                ApiKeyField("managementApiKey", "OPENROUTER_MANAGEMENT_API_KEY", "Management API-ключ"),
                BaseUrl(),
                Plain("httpReferer", "HTTP Referer", "Необязательный referer для OpenRouter.", "OPENROUTER_HTTP_REFERER"),
                Plain("clientTitle", "Название клиента", "Название клиента в OpenRouter.", "OPENROUTER_X_TITLE", "TokensLimitsExtension"),
            ],
            sourceDescription: "OpenRouter credits/key API; дополнительные поля управляют только заголовками и management-доступом.") ,
        CookieProvider("perplexity", "Perplexity", "https://www.perplexity.ai/account/usage"),
        Api("poe", "Poe", "https://poe.com/api/keys", "POE_API_KEY", withBaseUrl: true),
        CookieProvider("qoder", "Qoder", "https://qoder.com/account/usage"),
        CookieProvider("qwencloud", "Qwen Cloud", "https://bailian.console.aliyun.com"),
        CookieProvider("sakana", "Sakana AI", "https://console.sakana.ai/billing"),
        new(
            "stepfun",
            "StepFun",
            UsageProviderAuthKind.ApiKey,
            "https://platform.stepfun.com/plan-usage",
            settings:
            [
                ApiKeyField("apiKey", "STEPFUN_TOKEN", "Oasis-Token"),
            ],
            sourceDescription: "Официальный StepFun Dashboard API; нужен действующий Oasis-Token."),
        Api("sub2api", "sub2api", "", "SUB2API_API_KEY", withBaseUrl: true),
        Api("synthetic", "Synthetic", "", "SYNTHETIC_API_KEY", withBaseUrl: true),
        CookieProvider("t3chat", "T3 Chat", "https://t3.chat/settings/subscription"),
        Api("venice", "Venice", "https://venice.ai/settings/api", "VENICE_API_KEY", withBaseUrl: true),
        new("vertexai", "Vertex AI", UsageProviderAuthKind.OAuth, "https://console.cloud.google.com/vertex-ai", settings: [new("oauthToken", "Google OAuth token", "OAuth access token Vertex AI.", true), Project(), Region()]),
        Api("warp", "Warp", "https://docs.warp.dev/reference/cli/api-keys", "WARP_API_KEY", withBaseUrl: true),
        Api("wayfinder", "Wayfinder", "", "WAYFINDER_API_KEY", withBaseUrl: true),
        new(
            "windsurf",
            "Windsurf",
            UsageProviderAuthKind.Cookie,
            "https://windsurf.com/subscription/usage",
            settings:
            [
                new("sessionBundle", "Windsurf session bundle", "JSON или key=value с devin_session_token, devin_auth1_token, devin_account_id и devin_primary_org_id.", true, "WINDSURF_SESSION_BUNDLE"),
                Cookie(),
            ],
            sourceDescription: "Официальный Windsurf GetPlanStatus protobuf API; нужен session bundle из авторизованной сессии Windsurf/Devin."),
        Api("xai", "xAI", "https://console.x.ai", "XAI_MANAGEMENT_API_KEY", withBaseUrl: true, withAccount: true),
        new(
            "zai",
            "z.ai",
            UsageProviderAuthKind.ApiKey,
            "https://z.ai/manage-apikey/coding-plan/personal/my-plan",
            settings:
            [
                ApiKey("Z_AI_API_KEY"),
                Plain("region", "Регион API", "global или bigmodel-cn.", "Z_AI_REGION", "global"),
                Plain("scope", "Область квоты", "personal или team.", "Z_AI_USAGE_SCOPE", "personal"),
                Account("Организация"),
                Project(),
                Plain("quotaEndpoint", "Quota endpoint", "Необязательный URL quota endpoint.", "Z_AI_QUOTA_ENDPOINT"),
                Plain("modelUsageEndpoint", "Model usage endpoint", "Необязательный URL model usage endpoint.", "Z_AI_MODEL_USAGE_ENDPOINT"),
                Plain("balanceEndpoint", "Balance endpoint", "Необязательный URL balance endpoint.", "Z_AI_BALANCE_ENDPOINT"),
            ],
            sourceDescription: "Официальный z.ai quota/model-usage API с поддержкой global и BigModel CN.") ,
        new(
            "zed",
            "Zed",
            UsageProviderAuthKind.ApiKey,
            "https://cloud.zed.dev/client/users/me",
            settings:
            [
                ApiKeyField("apiKey", "ZED_ACCESS_TOKEN", "Zed access token"),
                Plain("userId", "Zed user ID", "Числовой GitHub/Zed user ID из локальных credentials.", "ZED_USER_ID"),
            ],
            sourceDescription: "Официальный Zed cloud profile API; нужен user ID и access token из авторизованного клиента Zed."),
        Api("zenmux", "ZenMux", "https://zenmux.ai/platform/management", "ZENMUX_API_KEY", withBaseUrl: true),
        CookieProvider("zoommate", "ZoomMate", "https://zoommate.zoom.us/#/?settings=credit-usage"),
    ];
}
