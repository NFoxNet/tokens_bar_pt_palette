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
    public void EntryShowsFiveClickableSectionsWithoutProviderFields(string language, params string[] titles)
    {
        using var fixture = new Fixture();
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference(language);
        var card = fixture.Card();
        Assert.Empty(InputIds(card));
        Assert.Equal(titles, RootSections(card).Select(section => HeaderTitle(section)));
        Assert.Equal(Sections, RootSections(card).Select(section =>
        {
            var action = section["items"]![0]!["selectAction"]!;
            Assert.Equal("Action.Submit", action["type"]!.GetValue<string>());
            Assert.Equal("none", action["associatedInputs"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(action["title"]!.GetValue<string>()));
            return action["data"]!["settingsSection"]!.GetValue<string>();
        }));
    }

    [Fact]
    public void SectionAndProviderNavigationExposeOnlyTheSelectedFieldsAndUseProviderLogos()
    {
        using var fixture = new Fixture();
        var form = fixture.Form();
        NavigateSection(form, "providers");
        var providers = JsonNode.Parse(form.TemplateJson)!;
        Assert.Empty(InputIds(providers));
        var providerHeaders = ProviderHeaders(ExpandedBody(Section(providers, 1)));
        Assert.Equal(UsageProviderDescriptorRegistry.All.Count, providerHeaders.Length);
        foreach (var header in providerHeaders)
        {
            var action = header["selectAction"]!;
            var providerId = action["data"]!["settingsProvider"]!.GetValue<string>();
            var logo = header["items"]![0]!["columns"]![0]!["items"]![0]!;
            Assert.Equal("Image", logo["type"]!.GetValue<string>());
            var imageUrl = logo["url"]!.GetValue<string>();
            Assert.StartsWith("data:image/png;base64,", imageUrl);
            var actualBytes = Convert.FromBase64String(imageUrl[(imageUrl.IndexOf(',', StringComparison.Ordinal) + 1)..]);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, actualBytes[..8]);
            var assetId = providerId switch
            {
                "codex" or "abacus" or "aiand" or "deepseek" => providerId,
                "alibabatokenplan" => "alibaba",
                _ => null,
            };
            if (assetId is not null)
            {
                var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Providers", $"ProviderIcon-{assetId}.64.png");
                Assert.Equal(File.ReadAllBytes(assetPath), actualBytes);
            }
        }

        NavigateProvider(form, "codex");
        var codex = JsonNode.Parse(form.TemplateJson)!;
        var codexSection = Section(codex, 1);
        Assert.Contains("tokensLimits.providers.codex.enabled", InputIds(ExpandedBody(codexSection)));
        Assert.All(InputIds(codex), id => Assert.StartsWith("tokensLimits.providers.codex.", id));
        Assert.DoesNotContain("tokensLimits.providers.deepseek.enabled", InputIds(codex));
    }

    [Fact]
    public void DependentFieldsAppearOnlyForEnabledFeaturesAndProviders()
    {
        using var fixture = new Fixture();
        var form = fixture.Form();
        NavigateSection(form, "notifications");
        Assert.DoesNotContain("tokensLimits.notifications.threshold", InputIds(JsonNode.Parse(form.TemplateJson)));
        NavigateSection(form, "history");
        Assert.DoesNotContain("tokensLimits.history.retentionDays", InputIds(JsonNode.Parse(form.TemplateJson)));
        NavigateSection(form, "dock");
        Assert.Contains("tokensLimits.dock.providers.codex.visible", InputIds(JsonNode.Parse(form.TemplateJson)));
        Assert.DoesNotContain("tokensLimits.dock.providers.deepseek.visible", InputIds(JsonNode.Parse(form.TemplateJson)));

        fixture.Settings.Settings.Update("{\"tokensLimits.notifications.enabled\":\"true\",\"tokensLimits.history.enabled\":\"true\",\"tokensLimits.providers.deepseek.enabled\":\"true\"}");
        NavigateSection(form, "notifications");
        Assert.Contains("tokensLimits.notifications.threshold", InputIds(JsonNode.Parse(form.TemplateJson)));
        NavigateSection(form, "history");
        Assert.Contains("tokensLimits.history.retentionDays", InputIds(JsonNode.Parse(form.TemplateJson)));
        NavigateSection(form, "dock");
        Assert.Contains("tokensLimits.dock.providers.deepseek.visible", InputIds(JsonNode.Parse(form.TemplateJson)));
    }

    [Fact]
    public void SavingGeneralPreservesHiddenSettingsAndProtectedSecretsAcrossReload()
    {
        using var fixture = new Fixture();
        var form = fixture.Form();
        NavigateSection(form, "providers");
        NavigateProvider(form, "deepseek");
        form.SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"test-only-secret\",\"tokensLimits.providers.deepseek.enabled\":\"true\"}", "{}");
        NavigateSection(form, "history");
        form.SubmitForm("{\"tokensLimits.history.retentionDays\":\"30\",\"tokensLimits.history.enabled\":\"true\"}", "{}");
        NavigateSection(form, "dock");
        form.SubmitForm("{\"tokensLimits.dock.providers.deepseek.visible\":\"false\"}", "{}");
        NavigateSection(form, "general");
        Assert.DoesNotContain("test-only-secret", form.TemplateJson);
        AssertSaveScopes(JsonNode.Parse(form.TemplateJson)!);

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
        var form = fixture.Form();
        NavigateSection(form, "notifications");
        form.SubmitForm("{\"tokensLimits.notifications.threshold\":\"35\"}", "{}");
        form.SubmitForm("{\"tokensLimits.notifications.enabled\":\"true\"}", "{}");
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        form.SubmitForm("{\"tokensLimits.notifications.threshold\":\"101\"}", "{}");
        Assert.Equal(35, fixture.Settings.NotificationThresholdPercent);
        var section = Section(JsonNode.Parse(form.TemplateJson)!, 3);
        Assert.Contains("tokensLimits.notifications.threshold", InputIds(ExpandedBody(section)));
        Assert.Contains("Enter a whole percent", section.ToJsonString());
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
        var form = fixture.Form();
        var visible = new List<string>();
        foreach (var sectionId in Sections)
        {
            NavigateSection(form, sectionId);
            var card = JsonNode.Parse(form.TemplateJson)!;
            visible.AddRange(InputIds(card));
            if (sectionId == "providers")
                foreach (var descriptor in UsageProviderDescriptorRegistry.All)
                {
                    NavigateProvider(form, descriptor.Id);
                    var providerCard = JsonNode.Parse(form.TemplateJson)!;
                    visible.AddRange(InputIds(providerCard));
                    AssertSaveScopes(providerCard);
                }
            else
                AssertSaveScopes(card);
        }
        var native = JsonNode.Parse(Assert.IsType<SettingsForm>(Assert.Single(fixture.Settings.Settings.ToContent())).TemplateJson)!;
        Assert.Equal(AllInputIds(native).Order(), visible.Distinct().Order());
    }

    [Fact]
    public void ClearingOneProviderSecretPreservesOtherProviderSecrets()
    {
        using var fixture = new Fixture();
        var form = fixture.Form();
        NavigateSection(form, "providers");
        NavigateProvider(form, "deepseek");
        form.SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"test-deepseek-secret\"}", "{}");
        NavigateProvider(form, "openai");
        form.SubmitForm("{\"tokensLimits.providers.openai.apiKey\":\"test-openai-secret\"}", "{}");
        NavigateProvider(form, "deepseek");
        form.SubmitForm("{\"tokensLimits.providers.deepseek.apiKey\":\"\"}", "{}");
        using var restored = new TokensLimitsSettings(fixture.Directory);
        Assert.Null(restored.GetValue("deepseek", "apiKey"));
        Assert.Equal("test-openai-secret", restored.GetValue("openai", "apiKey"));
        Assert.DoesNotContain("test-openai-secret", form.TemplateJson);
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
            Assert.Equal(5, RootSections(JsonNode.Parse(form.TemplateJson)!).Length);
        }
        finally { WinRT.MarshalInterface<IFormContent>.DisposeAbi(formPointer); }
    }

    [Fact]
    public void NavigationDoesNotSaveSettingsCanCollapseAndRejectsUnknownRoutes()
    {
        using var fixture = new Fixture();
        var form = fixture.Form();
        var path = fixture.Settings.FilePath;
        var before = File.ReadAllText(path);
        var changes = 0;
        EventHandler handler = (_, _) => changes++;
        fixture.Settings.Changed += handler;
        try
        {
            NavigateSection(form, "general");
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(0, changes);
            NavigateSection(form, "general");
            Assert.Empty(InputIds(JsonNode.Parse(form.TemplateJson)));
            Assert.Throws<ArgumentException>(() => form.SubmitForm("{}", "{\"settingsSection\":\"unknown\"}"));
            NavigateSection(form, "providers");
            Assert.Throws<ArgumentException>(() => form.SubmitForm("{}", "{\"settingsProvider\":\"unknown\"}"));
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
    public void SectionHeadersUseThemeCardsAndShowFeatureState(string language, string off, string on)
    {
        using var fixture = new Fixture();
        fixture.Settings.Settings.Update($"{{\"tokensLimits.language\":\"{language}\"}}");
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference(language);
        var form = fixture.Form();
        var card = JsonNode.Parse(form.TemplateJson)!;
        foreach (var section in RootSections(card))
        {
            Assert.Equal("Container", section["type"]!.GetValue<string>());
            Assert.Equal("emphasis", section["style"]!.GetValue<string>());
            var header = section["items"]![0]!;
            Assert.Equal("Container", header["type"]!.GetValue<string>());
            Assert.Equal("Action.Submit", header["selectAction"]!["type"]!.GetValue<string>());
            Assert.Equal("stretch", header["items"]![0]!["columns"]![1]!["width"]!.GetValue<string>());
            Assert.All(header["items"]![0]!["columns"]![1]!["items"]!.AsArray(), text => Assert.True(text!["wrap"]!.GetValue<bool>()));
            Assert.Contains(header["items"]![0]!["columns"]!.AsArray(), column => column!["items"]!.AsArray().Any(item => item!["text"]?.GetValue<string>() is "⌄" or "⌃"));
        }
        Assert.Equal(off, StateText(Section(card, 3)));
        NavigateSection(form, "notifications");
        form.SubmitForm("{\"tokensLimits.notifications.enabled\":\"true\"}", "{}");
        var updated = JsonNode.Parse(form.TemplateJson)!;
        Assert.Equal(on, StateText(Section(updated, 3)));
        Assert.Contains("tokensLimits.notifications.threshold", InputIds(ExpandedBody(Section(updated, 3))));
        NavigateSection(form, "notifications");
        Assert.Empty(InputIds(JsonNode.Parse(form.TemplateJson)));
    }

    private static string StateText(JsonNode section) => section["items"]![0]!["items"]![0]!["columns"]![2]!["items"]![0]!["text"]!.GetValue<string>();
    private static JsonNode[] RootSections(JsonNode card) => card["body"]!.AsArray().Select(section => section!).ToArray();
    private static JsonNode Section(JsonNode card, int index) => RootSections(card)[index];
    private static string HeaderTitle(JsonNode section) => section["items"]![0]!["items"]![0]!["columns"]![1]!["items"]![0]!["text"]!.GetValue<string>();
    private static JsonNode ExpandedBody(JsonNode section) => section["items"]![1]!;
    private static JsonNode[] ProviderHeaders(JsonNode body) => body["items"]!.AsArray().Select(item => item!["items"]![0]!).ToArray();

    private static void NavigateSection(FormContent form, string id) => form.SubmitForm("{}", new JsonObject { ["settingsSection"] = id }.ToJsonString());
    private static void NavigateProvider(FormContent form, string id) => form.SubmitForm("{}", new JsonObject { ["settingsProvider"] = id }.ToJsonString());

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

    private static string[] InputIds(JsonNode? node) => AllInputIds(node);

    private static void AssertSaveScopes(JsonNode card)
    {
        var ids = InputIds(card).Order().ToArray();
        var actions = card["body"]!.AsArray().SelectMany(item => FindSaveActions(item)).ToArray();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
            Assert.Equal(ids, action["data"]!.AsObject().Select(pair => pair.Key).Order());
    }

    private static IEnumerable<JsonNode> FindSaveActions(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "ActionSet")
            {
                foreach (var action in obj["actions"]!.AsArray())
                    if (action!["type"]?.GetValue<string>() == "Action.Submit") yield return action;
                yield break;
            }
            foreach (var value in obj.Select(pair => pair.Value))
                foreach (var action in FindSaveActions(value)) yield return action;
        }
        else if (node is JsonArray array)
            foreach (var value in array)
                foreach (var action in FindSaveActions(value)) yield return action;
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
