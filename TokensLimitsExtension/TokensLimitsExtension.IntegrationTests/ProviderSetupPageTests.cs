using System.Collections;
using System.Reflection;
using System.Net;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Core.Services;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class ProviderSetupPageTests
{
    [Fact]
    public void SetupListsDisabledProvidersAndShowsRequiredFieldsWithoutSecretValues()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, "tokensLimits.providers.azureopenai.apiKey", "sample-secret-value");
        using var coordinator = new UsageRefreshCoordinator(settings);
        using var page = new ProviderSetupPage(settings, [], coordinator);

        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == "Azure OpenAI");

        Assert.Contains(settings.Localization.GetString("setup.requiredFields", "Required fields"), item.Details.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(settings.Localization.GetString("settings.field.apiKey.label", "API key"), item.Details.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(settings.Localization.GetString("settings.field.deploymentName.label", "Deployment"), item.Details.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sample-secret-value", item.Details.Body, StringComparison.Ordinal);
        Assert.Contains(settings.Localization.GetString("setup.status.disabled", "Disabled · open settings to enable"), item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(item.MoreCommands!, command =>
            command is CommandContextItem context && context.Command?.GetType().Name == "SettingsContentPage");
        Assert.DoesNotContain(item.MoreCommands!, command =>
            command is CommandContextItem context && context.Title.Contains("validate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnabledProviderWithMissingCredentialsExplainsAuthenticationAndKeepsSettingsAction()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, "tokensLimits.providers.azureopenai.enabled", "true");
        using var coordinator = new UsageRefreshCoordinator(settings);
        using var page = new ProviderSetupPage(settings, [], coordinator);

        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == "Azure OpenAI");

        Assert.Contains(settings.Localization.GetString("setup.status.authRequired", "Authentication required"), item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(item.MoreCommands!, command =>
            command is CommandContextItem context && context.Command?.GetType().Name == "SettingsContentPage");
    }

    [Fact]
    public async Task SetupDistinguishesQuotaUnavailableFromConnectionFailureWithoutRefreshingDuringGetItems()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, "tokensLimits.providers.deepseek.enabled", "true");
        SetSetting(settings, "tokensLimits.providers.deepseek.apiKey", "sample-test-key");
        var provider = new TestProvider(UsageProviderDescriptorRegistry.All.Single(item => item.Id == "deepseek"),
            snapshot: new UsageSnapshot("deepseek", "DeepSeek", null, null, null, false)
            {
                Metrics = [new UsageMetric("Connection", "Configured")],
            });
        using var cache = new UsageSnapshotCache(provider, settings);
        using var coordinator = new UsageRefreshCoordinator(settings);
        coordinator.UpdateProviders([cache]);
        await cache.GetUsageSnapshotAsync();
        using var page = new ProviderSetupPage(settings, [cache], coordinator);
        var itemsChanged = 0;
        page.ItemsChanged += (_, _) => itemsChanged++;

        var quotaStatus = Assert.Single(page.GetItems(), item => item.Title == "DeepSeek").Subtitle;
        Assert.Contains(settings.Localization.GetString("setup.status.quotaUnavailable", "Connected · quota unavailable"), quotaStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, provider.CallCount);

        provider.Failure = new HttpRequestException("offline");
        await cache.RefreshAsync(force: true);
        var connectionFailure = Assert.Single(page.GetItems(), item => item.Title == "DeepSeek").Subtitle;

        Assert.Contains(settings.Localization.GetString("setup.status.temporaryError", "Temporary connection error"), connectionFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, provider.CallCount);
        Assert.True(itemsChanged > 0);
    }

    [Fact]
    public async Task EmptySnapshotAndProviderFailureHaveDistinctSafeStatuses()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, "tokensLimits.providers.deepseek.enabled", "true");
        SetSetting(settings, "tokensLimits.providers.deepseek.apiKey", "sample-test-key");
        var provider = new TestProvider(
            UsageProviderDescriptorRegistry.All.Single(item => item.Id == "deepseek"),
            new UsageSnapshot("deepseek", "DeepSeek", null, null, null, false));
        using var cache = new UsageSnapshotCache(provider, settings);
        using var coordinator = new UsageRefreshCoordinator(settings);
        coordinator.UpdateProviders([cache]);
        await cache.GetUsageSnapshotAsync();
        using var page = new ProviderSetupPage(settings, [cache], coordinator);

        var emptyStatus = Assert.Single(page.GetItems(), item => item.Title == "DeepSeek").Subtitle;
        Assert.Contains(settings.Localization.GetString("setup.status.unavailable", "Provider data unavailable"), emptyStatus, StringComparison.OrdinalIgnoreCase);

        provider.Failure = new InvalidDataException("private-response-payload");
        await cache.RefreshAsync(force: true);
        var failure = Assert.Single(page.GetItems(), item => item.Title == "DeepSeek");
        Assert.Contains(settings.Localization.GetString("setup.status.unavailable", "Provider data unavailable"), failure.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-response-payload", failure.Subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-payload", failure.Details.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai", "adminApiKey")]
    [InlineData("windsurf", "sessionBundle")]
    public void ProviderSpecificAuthenticationFieldSatisfiesSetupRequirements(string providerId, string requiredKey)
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, $"tokensLimits.providers.{providerId}.enabled", "true");
        SetSetting(settings, $"tokensLimits.providers.{providerId}.{requiredKey}", "configured-secret");
        using var coordinator = new UsageRefreshCoordinator(settings);
        using var page = new ProviderSetupPage(settings, [], coordinator);

        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == (providerId == "openai" ? "OpenAI" : "Windsurf"));

        Assert.Contains(settings.Localization.GetString("setup.status.waiting", "Configured · waiting for provider data"), item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configured-secret", item.Details.Body, StringComparison.Ordinal);
        if (providerId == "windsurf")
        {
            Assert.Contains(settings.Localization.GetString("settings.field.sessionBundle.label", "Windsurf session bundle"), item.Details.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookieHeader", item.Details.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("tokensLimits.providers.azureopenai.apiKey", "auth")]
    [InlineData("tokensLimits.providers.azureopenai.baseUrl", "configuration")]
    [InlineData("tokensLimits.providers.azureopenai.deploymentName", "configuration")]
    public void AzureRequiredSettingsAreReportedWithTheRightSetupStatus(string settingKey, string expected)
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetAzureSettings(settings);
        SetSetting(settings, settingKey, string.Empty);
        using var coordinator = new UsageRefreshCoordinator(settings);
        using var page = new ProviderSetupPage(settings, [], coordinator);

        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == "Azure OpenAI");

        var key = expected == "auth" ? "setup.status.authRequired" : "setup.status.configurationRequired";
        var fallback = expected == "auth" ? "Authentication required" : "Setup incomplete";
        Assert.Contains(settings.Localization.GetString(key, fallback), item.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZedUserIdIsRequiredAlongsideApiKey()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetSetting(settings, "tokensLimits.providers.zed.enabled", "true");
        SetSetting(settings, "tokensLimits.providers.zed.apiKey", "configured-key");
        using var coordinator = new UsageRefreshCoordinator(settings);
        using var page = new ProviderSetupPage(settings, [], coordinator);

        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == "Zed");

        Assert.Contains(settings.Localization.GetString("setup.status.configurationRequired", "Setup incomplete"), item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(settings.Localization.GetString("settings.field.userId.label", "Zed user ID"), item.Details.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureSetupRequiresExplicitValidationAndReflectsItsSuccess()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetAzureSettings(settings);
        var handler = new AzureHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        using var provider = CreateAzureProvider(settings, httpClient);
        using var cache = new UsageSnapshotCache(provider, settings);
        using var coordinator = new UsageRefreshCoordinator(settings);
        await cache.GetUsageSnapshotAsync();
        Assert.Equal(0, handler.RequestCount);
        using var page = new ProviderSetupPage(settings, [cache], coordinator);

        var beforeValidation = Assert.Single(page.GetItems(), item => item.Title == "Azure OpenAI").Subtitle;
        Assert.Contains(settings.Localization.GetString("setup.status.validationRequired", "Connection validation required"), beforeValidation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);

        await cache.ValidateConnectionAsync();
        var afterValidation = Assert.Single(page.GetItems(), item => item.Title == "Azure OpenAI").Subtitle;

        Assert.Contains(settings.Localization.GetString("status.connection.validated", "Connection validated"), afterValidation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(settings.Localization.GetString("setup.status.quotaUnavailable", "Quota unavailable"), afterValidation, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AzureExplicitValidationFailureShowsSafeAuthenticationStatus()
    {
        using var directory = new TestDirectory();
        using var settings = new TokensLimitsSettings(directory.Path);
        SetAzureSettings(settings);
        var handler = new AzureHandler(HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);
        using var provider = CreateAzureProvider(settings, httpClient);
        using var cache = new UsageSnapshotCache(provider, settings);
        using var coordinator = new UsageRefreshCoordinator(settings);
        await cache.GetUsageSnapshotAsync();
        Assert.Equal(0, handler.RequestCount);
        using var page = new ProviderSetupPage(settings, [cache], coordinator);

        await Assert.ThrowsAsync<UsageProviderRequestException>(() => cache.ValidateConnectionAsync());
        var item = Assert.Single(page.GetItems(), candidate => candidate.Title == "Azure OpenAI");

        Assert.Contains(settings.Localization.GetString("setup.status.authRequired", "Authentication required"), item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unauthorized", item.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    private static void SetAzureSettings(TokensLimitsSettings settings)
    {
        SetSetting(settings, "tokensLimits.providers.azureopenai.enabled", "true");
        SetSetting(settings, "tokensLimits.providers.azureopenai.apiKey", "sample-test-key");
        SetSetting(settings, "tokensLimits.providers.azureopenai.baseUrl", "https://example.openai.azure.com");
        SetSetting(settings, "tokensLimits.providers.azureopenai.deploymentName", "test-deployment");
    }

    private static ConfiguredUsageProvider CreateAzureProvider(TokensLimitsSettings settings, HttpClient httpClient)
        => new(UsageProviderDescriptorRegistry.All.Single(item => item.Id == "azureopenai"), settings, httpClient,
            logger: null, requestTimeout: TimeSpan.FromSeconds(2), maxResponseBodyBytes: 1_024);

    private sealed class AzureHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent("{}") });
        }
    }

    private static void SetSetting(TokensLimitsSettings settings, string key, string value)
    {
        var field = typeof(Microsoft.CommandPalette.Extensions.Toolkit.Settings).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic);
        var registered = Assert.IsAssignableFrom<IDictionary>(field?.GetValue(settings.Settings));
        switch (registered[key])
        {
            case TextSetting text:
                text.Value = value;
                break;
            case ToggleSetting toggle:
                toggle.Value = bool.Parse(value);
                break;
            default:
                throw new InvalidOperationException($"Unexpected setting type for '{key}'.");
        }
    }

    private sealed class TestProvider(UsageProviderDescriptor descriptor, UsageSnapshot? snapshot = null) : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor { get; } = descriptor;
        public Exception? Failure { get; set; }
        public int CallCount { get; private set; }

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Failure is { } failure
                ? Task.FromException<UsageSnapshot>(failure)
                : Task.FromResult(snapshot!);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tokens-limits-provider-setup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
