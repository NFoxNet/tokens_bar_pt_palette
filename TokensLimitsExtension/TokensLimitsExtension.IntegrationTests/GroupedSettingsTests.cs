using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Localization;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class GroupedSettingsTests
{
    [Theory]
    [InlineData("en", "General", "Providers", "Dock", "Notifications", "History")]
    [InlineData("ru", "Общие", "Провайдеры", "Dock", "Уведомления", "История")]
    public void EntryShowsFiveSectionsWithoutExposingProviderFields(string language, params string[] titles)
    {
        using var fixture = new Fixture();
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference(language);
        var card = fixture.Card();
        Assert.Empty(InputIds(card["body"]));
        Assert.Equal(titles, ShowCards(card).Select(action => action["title"]!.GetValue<string>()));
        Assert.All(ShowCards(card), action => Assert.Equal("Action.ShowCard", action!["type"]!.GetValue<string>()));
        var providers = Section(card, 1);
        Assert.Empty(InputIds(providers["body"]));
        Assert.Equal(UsageProviderDescriptorRegistry.All.Count, ShowCards(providers).Length);
        var codex = ShowCards(providers).Single(action => action["title"]!.GetValue<string>() == "Codex")!["card"]!;
        Assert.Contains("tokensLimits.providers.codex.enabled", InputIds(codex["body"]));
        Assert.All(InputIds(codex["body"]), id => Assert.StartsWith("tokensLimits.providers.codex.", id));
    }

    [Fact]
    public void DependentFieldsAppearOnlyForEnabledFeaturesAndProviders()
    {
        using var fixture = new Fixture();
        var before = fixture.Card();
        Assert.DoesNotContain("tokensLimits.notifications.threshold", InputIds(Section(before, 3)["body"]));
        Assert.DoesNotContain("tokensLimits.history.retentionDays", InputIds(Section(before, 4)["body"]));
        Assert.Contains("tokensLimits.dock.providers.codex.visible", InputIds(Section(before, 2)["body"]));
        Assert.DoesNotContain("tokensLimits.dock.providers.deepseek.visible", InputIds(Section(before, 2)["body"]));
        fixture.Settings.Settings.Update("{\"tokensLimits.notifications.enabled\":\"true\",\"tokensLimits.history.enabled\":\"true\",\"tokensLimits.providers.deepseek.enabled\":\"true\"}");
        var after = fixture.Card();
        Assert.Contains("tokensLimits.notifications.threshold", InputIds(Section(after, 3)["body"]));
        Assert.Contains("tokensLimits.history.retentionDays", InputIds(Section(after, 4)["body"]));
        Assert.Contains("tokensLimits.dock.providers.deepseek.visible", InputIds(Section(after, 2)["body"]));
    }

    [Fact]
    public void SavingGeneralPreservesHiddenSettingsAndProtectedSecretsAcrossReload()
    {
        using var fixture = new Fixture();
        fixture.Form().SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"test-only-secret\",\"tokensLimits.providers.deepseek.enabled\":\"true\",\"tokensLimits.history.retentionDays\":\"30\",\"tokensLimits.history.enabled\":\"true\",\"tokensLimits.dock.providers.deepseek.visible\":\"false\"}", "{}");
        var form = fixture.Form();
        Assert.DoesNotContain("test-only-secret", form.TemplateJson);
        var general = Section(JsonNode.Parse(form.TemplateJson)!, 0);
        Assert.All(general["actions"]![0]!["data"]!.AsObject(), pair => Assert.Contains(pair.Key, InputIds(general["body"])));
        form.SubmitForm("{\"tokensLimits.refreshInterval\":\"120\",\"tokensLimits.language\":\"ru\"}", "{}");
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
        fixture.Form().SubmitForm("{\"tokensLimits.notifications.threshold\":\"35\"}", "{}");
        fixture.Form().SubmitForm("{\"tokensLimits.notifications.enabled\":\"true\"}", "{}");
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        fixture.Form().SubmitForm("{\"tokensLimits.notifications.threshold\":\"101\"}", "{}");
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        var section = Section(fixture.Card(), 3);
        Assert.Contains("tokensLimits.notifications.threshold", InputIds(section["body"]));
        Assert.Contains("Enter a whole percent", section.ToJsonString());
    }

    [Fact]
    public void EveryRegisteredSettingRemainsReachableAndSubmitPayloadsStayWithinTheirCards()
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
        var grouped = fixture.Card();
        var native = JsonNode.Parse(Assert.IsType<SettingsForm>(Assert.Single(fixture.Settings.Settings.ToContent())).TemplateJson)!;
        Assert.Equal(AllInputIds(native).Order(), AllInputIds(grouped).Order());
        AssertSubmitScopes(grouped);
    }

    [Fact]
    public void ClearingOneProviderSecretPreservesOtherProviderSecrets()
    {
        using var fixture = new Fixture();
        fixture.Form().SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"test-deepseek-secret\",\"tokensLimits.providers.openai.apiKey\":\"test-openai-secret\"}", "{}");
        fixture.Form().SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"\"}", "{}");
        using var restored = new TokensLimitsSettings(fixture.Directory);
        Assert.Null(restored.GetValue("deepseek", "apiKey"));
        Assert.Equal("test-openai-secret", restored.GetValue("openai", "apiKey"));
        Assert.DoesNotContain("test-openai-secret", fixture.Form().TemplateJson);
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
        }
        finally { WinRT.MarshalInterface<ICommandSettings>.DisposeAbi(settingsPointer); }

        var formPointer = WinRT.MarshalInterface<IFormContent>.FromManaged(fixture.Form());
        try
        {
            Assert.NotEqual(IntPtr.Zero, formPointer);
            var form = WinRT.MarshalInterface<IFormContent>.FromAbi(formPointer);
            Assert.Equal(5, ShowCards(JsonNode.Parse(form.TemplateJson)!).Length);
        }
        finally { WinRT.MarshalInterface<IFormContent>.DisposeAbi(formPointer); }
    }
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

    private static void AssertSubmitScopes(JsonNode card)
    {
        if (card["actions"] is JsonArray actions)
            foreach (var action in actions)
                Assert.Equal(InputIds(card["body"]).Order(), action!["data"]!.AsObject().Select(pair => pair.Key).Order());
        foreach (var section in ShowCards(card)) AssertSubmitScopes(section["card"]!);
    }
    private static JsonNode[] ShowCards(JsonNode card) => card["body"]!.AsArray().Where(node => node?["type"]?.GetValue<string>() == "ActionSet").Select(node => node!["actions"]![0]!).ToArray();

    private static JsonNode Section(JsonNode card, int index) => ShowCards(card)[index]["card"]!;

    private static string[] InputIds(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal))
                return [obj["id"]!.GetValue<string>()];
            return obj.Where(pair => pair.Key != "card").SelectMany(pair => InputIds(pair.Value)).ToArray();
        }
        return node is JsonArray array ? array.SelectMany(InputIds).ToArray() : [];
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

        public FormContent Form() => Assert.IsAssignableFrom<FormContent>(Assert.Single(_provider.Settings!.SettingsPage.GetContent()));
        public JsonNode Card() => JsonNode.Parse(Form().TemplateJson)!;
        public void Dispose()
        {
            _provider.Dispose();
            _registry.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
