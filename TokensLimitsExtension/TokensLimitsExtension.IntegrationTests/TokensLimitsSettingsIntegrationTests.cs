using TokensLimitsExtension.Settings;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Localization;
using TokensLimitsExtension.Core.Providers;
using System.Collections;
using System.Reflection;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class TokensLimitsSettingsIntegrationTests
{
    [Fact]
    public void ResolveStorageMigratesMatchingSettingsAndSecretsPair()
    {
        using var canonical = new TestDirectory();
        using var host = new TestDirectory();
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.settings.json"), "stale-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.settings.json"), "current-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.secrets.json"), "current-secrets");

        var storage = TokensLimitsSettings.ResolveStorage(canonical.Path, host.Path);

        Assert.Equal("current-settings", File.ReadAllText(storage.SettingsPath));
        Assert.Equal("current-secrets", File.ReadAllText(storage.SecretsPath));
    }

    [Fact]
    public void ResolveStorageKeepsExistingCanonicalPair()
    {
        using var canonical = new TestDirectory();
        using var host = new TestDirectory();
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.settings.json"), "canonical-settings");
        File.WriteAllText(System.IO.Path.Combine(canonical.Path, "tokensLimits.secrets.json"), "canonical-secrets");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.settings.json"), "host-settings");
        File.WriteAllText(System.IO.Path.Combine(host.Path, "tokensLimits.secrets.json"), "host-secrets");

        var storage = TokensLimitsSettings.ResolveStorage(canonical.Path, host.Path);

        Assert.Equal("canonical-settings", File.ReadAllText(storage.SettingsPath));
        Assert.Equal("canonical-secrets", File.ReadAllText(storage.SecretsPath));
    }

    [Fact]
    public void RefreshesEverySettingsTextFromTheSelectedLanguagePack()
    {
        using var storage = new TestDirectory();
        using var settings = new TokensLimitsSettings(storage.Path);

        Assert.IsType<JsonLocalizationService>(settings.Localization).ApplyPreference("en");

        var refreshInterval = GetRegisteredSetting<TextSetting>(settings, "tokensLimits.refreshInterval");
        var codexToggle = GetRegisteredSetting<ToggleSetting>(settings, "tokensLimits.providers.codex.enabled");
        var deepSeekApiKey = GetRegisteredSetting<TextSetting>(settings, "tokensLimits.providers.deepseek.apiKey");

        Assert.Equal("Refresh interval", refreshInterval.Label);
        Assert.Equal("Enable Codex", codexToggle.Label);
        Assert.Equal("API key", deepSeekApiKey.Label);
        Assert.Contains("environment variable", deepSeekApiKey.Description, StringComparison.OrdinalIgnoreCase);

        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var toggle = GetRegisteredSetting<ToggleSetting>(settings, $"tokensLimits.providers.{descriptor.Id}.enabled");
            Assert.Equal($"Enable {descriptor.DisplayName}", toggle.Label);

            foreach (var field in descriptor.Settings)
            {
                var setting = GetRegisteredSetting<TextSetting>(settings, $"tokensLimits.providers.{descriptor.Id}.{field.Key}");
                Assert.NotEqual($"settings.field.{field.Key}.label", setting.Label);
                Assert.NotEqual($"settings.field.{field.Key}.description", setting.Description);
            }
        }

        Assert.IsType<JsonLocalizationService>(settings.Localization).ApplyPreference("ru");
        Assert.Equal("Частота обновления", refreshInterval.Label);
        Assert.Equal("Включить Codex", codexToggle.Label);
        Assert.Equal("API-ключ", deepSeekApiKey.Label);
    }

    private static T GetRegisteredSetting<T>(TokensLimitsSettings settings, string key)
        where T : class
    {
        var field = typeof(Microsoft.CommandPalette.Extensions.Toolkit.Settings).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var registeredSettings = Assert.IsAssignableFrom<IDictionary>(field?.GetValue(settings.Settings));
        return Assert.IsType<T>(registeredSettings[key]);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"TokensLimitsExtension.SettingsTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
