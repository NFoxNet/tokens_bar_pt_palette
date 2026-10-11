using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Localization;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class GroupedSettingsTests
{
    private static readonly string[] Sections = ["general", "providers", "dock", "notifications", "history"];

    [Theory]
    [InlineData("en", "General", "Providers", "Dock", "Notifications", "History")]
    [InlineData("ru", "Общие", "Провайдеры", "Dock", "Уведомления", "История")]
    public void NativeSettingsExposeLocalizedSectionAndFieldMetadata(string language, params string[] titles)
    {
        using var fixture = new Fixture();
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference(language);
        var editor = new NativeSettingsEditor(fixture.Settings);
        var actualTitles = Sections.Select(section => fixture.Settings.Localization.GetString($"settings.section.{section}")).ToArray();
        Assert.Equal(titles, actualTitles);
        Assert.All(editor.KeysForSection("general"), key => Assert.False(string.IsNullOrWhiteSpace(Assert.Single(editor.Fields([key])).Title)));
    }

    [Fact]
    public void ProviderSectionsExposeLocalizedFieldsAndMaskSecrets()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
        {
            var providerFields = editor.Fields(NativeSettingsEditor.KeysForProvider(descriptor.Id));
            Assert.Contains(providerFields, field => field.Key == $"tokensLimits.providers.{descriptor.Id}.enabled");
            Assert.All(providerFields, field => Assert.False(string.IsNullOrWhiteSpace(field.Title)));
        }
        var secret = Assert.Single(editor.Fields(NativeSettingsEditor.KeysForProvider("deepseek")), field => field.IsSecret);
        Assert.Empty(secret.Value);
        editor.Save(NativeSettingsEditor.KeysForProvider("deepseek"), new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = "test-only-secret",
        });
        Assert.Equal("••••••••", Assert.Single(editor.Fields(NativeSettingsEditor.KeysForProvider("deepseek")), field => field.IsSecret).Value);
    }

    [Fact]
    public void DependentFieldsAppearOnlyForEnabledFeaturesAndProviders()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        Assert.Contains("tokensLimits.notifications.threshold", editor.KeysForSection("notifications"));
        Assert.Contains("tokensLimits.history.retentionDays", editor.KeysForSection("history"));
        Assert.DoesNotContain("tokensLimits.dock.providers.deepseek.visible", editor.KeysForSection("dock"));
        fixture.Settings.Settings.Update("{\"tokensLimits.providers.deepseek.enabled\":\"true\"}");
        Assert.Contains("tokensLimits.dock.providers.deepseek.visible", editor.KeysForSection("dock"));
    }

    [Fact]
    public void SavingGeneralPreservesHiddenSettingsAndProtectedSecretsAcrossReload()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        editor.Save(NativeSettingsEditor.KeysForProvider("deepseek"), new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = "test-only-secret",
            ["tokensLimits.providers.deepseek.enabled"] = "true",
        });
        editor.Save(editor.KeysForSection("history"), new Dictionary<string, string>
        {
            ["tokensLimits.history.retentionDays"] = "30",
            ["tokensLimits.history.enabled"] = "true",
        });
        editor.Save(editor.KeysForSection("dock"), new Dictionary<string, string>
        {
            ["tokensLimits.dock.providers.deepseek.visible"] = "false",
        });
        Assert.DoesNotContain("test-only-secret", File.ReadAllText(fixture.Settings.FilePath));

        editor.Save(editor.KeysForSection("general"), new Dictionary<string, string>
        {
            ["tokensLimits.refreshInterval"] = "120",
            ["tokensLimits.language"] = "ru",
        });
        using var restored = new TokensLimitsSettings(fixture.Directory);
        Assert.Equal(TimeSpan.FromSeconds(120), restored.RefreshInterval);
        Assert.True(restored.IsEnabled("deepseek"));
        Assert.Equal("test-only-secret", restored.GetValue("deepseek", "apiKey"));
        Assert.True(restored.HistoryEnabled);
        Assert.Equal(30, restored.HistoryRetentionDays);
        Assert.False(restored.IsDockVisible("deepseek"));
        Assert.DoesNotContain("test-only-secret", File.ReadAllText(restored.FilePath));
    }

    [Fact]
    public void NotificationsSaveDoesNotResetHiddenThresholdAndReportsInvalidInput()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        editor.Save(editor.KeysForSection("notifications"), new Dictionary<string, string>
        {
            ["tokensLimits.notifications.threshold"] = "35",
        });
        editor.Save(editor.KeysForSection("notifications"), new Dictionary<string, string>
        {
            ["tokensLimits.notifications.enabled"] = "true",
        });
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        var errors = editor.Save(editor.KeysForSection("notifications"), new Dictionary<string, string>
        {
            ["tokensLimits.notifications.threshold"] = "101",
        });
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        Assert.Contains("tokensLimits.notifications.threshold", errors.Keys);
        Assert.Contains("Enter a whole percent", errors["tokensLimits.notifications.threshold"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryRegisteredSettingRemainsReachableAndSaveActionsStayWithinVisibleFields()
    {
        using var fixture = new Fixture();
        var enabled = new JsonObject
        {
            ["tokensLimits.notifications.enabled"] = "true",
            ["tokensLimits.history.enabled"] = "true",
        };
        foreach (var descriptor in UsageProviderDescriptorRegistry.All)
            enabled[$"tokensLimits.providers.{descriptor.Id}.enabled"] = "true";
        fixture.Settings.Settings.Update(enabled.ToJsonString());
        var editor = new NativeSettingsEditor(fixture.Settings);
        var visible = Sections.SelectMany(editor.KeysForSection)
            .Concat(UsageProviderDescriptorRegistry.All.SelectMany(descriptor => NativeSettingsEditor.KeysForProvider(descriptor.Id)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var native = JsonNode.Parse(Assert.IsType<SettingsForm>(Assert.Single(fixture.Settings.Settings.ToContent())).TemplateJson)!;
        Assert.Equal(AllInputIds(native).Order(StringComparer.Ordinal), visible);
    }

    [Fact]
    public void ClearingOneProviderSecretPreservesOtherProviderSecrets()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        editor.Save(NativeSettingsEditor.KeysForProvider("deepseek"), new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = "test-deepseek-secret",
        });
        editor.Save(NativeSettingsEditor.KeysForProvider("openai"), new Dictionary<string, string>
        {
            ["tokensLimits.providers.openai.apiKey"] = "test-openai-secret",
        });
        editor.Save(NativeSettingsEditor.KeysForProvider("deepseek"), new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = string.Empty,
        });
        using var restored = new TokensLimitsSettings(fixture.Directory);
        Assert.Null(restored.GetValue("deepseek", "apiKey"));
        Assert.Equal("test-openai-secret", restored.GetValue("openai", "apiKey"));
        Assert.DoesNotContain("test-openai-secret", File.ReadAllText(restored.FilePath));
    }

    [Fact]
    public void GroupedSettingsAndFormExposeCommandPaletteComInterfaces()
    {
        using var fixture = new Fixture();
        var settingsPointer = WinRT.MarshalInterface<ICommandSettings>.FromManaged(fixture.Settings.CommandSettings);
        try
        {
            Assert.NotEqual(IntPtr.Zero, settingsPointer);
            var commandSettings = WinRT.MarshalInterface<ICommandSettings>.FromAbi(settingsPointer);
            Assert.NotNull(commandSettings.SettingsPage);
            Assert.NotEmpty(commandSettings.SettingsPage.GetContent());
        }
        finally { WinRT.MarshalInterface<ICommandSettings>.DisposeAbi(settingsPointer); }
    }

    [Fact]
    public void ReadingMetadataDoesNotSaveSettingsAndUnknownScopesAreRejected()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        var path = fixture.Settings.FilePath;
        var before = File.ReadAllText(path);
        var changes = 0;
        EventHandler handler = (_, _) => changes++;
        fixture.Settings.Changed += handler;
        try
        {
            Assert.NotEmpty(editor.Fields(editor.KeysForSection("general")));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(0, changes);
            Assert.Throws<ArgumentException>(() => editor.KeysForSection("unknown"));
            Assert.Throws<ArgumentException>(() => NativeSettingsEditor.KeysForProvider("unknown"));
            Assert.Throws<ArgumentException>(() => editor.Fields(["unknown"]));
            Assert.Throws<ArgumentException>(() => editor.Save(editor.KeysForSection("general"), new Dictionary<string, string>
            {
                ["tokensLimits.providers.deepseek.apiKey"] = "out-of-scope",
            }));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(0, changes);
        }
        finally
        {
            fixture.Settings.Changed -= handler;
        }
    }
    [Theory]
    [InlineData("en", "Off", "On")]
    [InlineData("ru", "Выкл.", "Вкл.")]
    public void NativeSettingsKeepLocalizedFeatureStateMetadata(string language, string off, string on)
    {
        using var fixture = new Fixture();
        fixture.Settings.Settings.Update($"{{\"tokensLimits.language\":\"{language}\"}}");
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference(language);
        var editor = new NativeSettingsEditor(fixture.Settings);
        Assert.Equal(off, fixture.Settings.Localization.GetString("settings.card.off"));
        Assert.Equal(on, fixture.Settings.Localization.GetString("settings.card.on"));
        var notificationKeys = editor.KeysForSection("notifications");
        Assert.Contains(notificationKeys, key => Assert.Single(editor.Fields([key])).Value == "false");
        Assert.Contains("tokensLimits.notifications.threshold", notificationKeys);
        editor.Save(notificationKeys, new Dictionary<string, string>
        {
            ["tokensLimits.notifications.enabled"] = "true",
        });
        Assert.Contains(notificationKeys, key => Assert.Single(editor.Fields([key])).Value == "true");
    }

    private static string StateText(JsonNode section) => section["items"]![0]!["items"]![0]!["columns"]![2]!["items"]![0]!["text"]!.GetValue<string>();
    private static string[] AllInputIds(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal))
                return [obj["id"]!.GetValue<string>()];
            return obj.SelectMany(pair => AllInputIds(pair.Value)).ToArray();
        }
        return node is JsonArray array ? array.SelectMany(AllInputIds).ToArray() : [];
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"TokensLimits.GroupedSettings.{Guid.NewGuid():N}");
        public TokensLimitsSettings Settings { get; }
        private readonly TokensLimitsExtensionCommandsProvider _provider;
        private readonly UsageProviderRegistry _registry = new([]);

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Settings = new TokensLimitsSettings(Directory);
            Settings.Settings.Update("{\"tokensLimits.language\":\"en\"}");
            Assert.IsType<JsonLocalizationService>(Settings.Localization).ApplyPreference("en");
            _provider = new TokensLimitsExtensionCommandsProvider(null, _registry, Settings);
        }

        public void Dispose()
        {
            _provider.Dispose();
            _registry.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
