using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Localization;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class NativeSettingsEditorTests
{
    private static readonly string[] Sections = ["general", "dock", "notifications", "history"];

    [Fact]
    public void EveryRegisteredSettingIsReachableThroughItsSectionOrProvider()
    {
        using var fixture = new Fixture();
        fixture.EnableAllProviders();
        var editor = new NativeSettingsEditor(fixture.Settings);
        var keys = Sections.SelectMany(editor.KeysForSection)
            .Concat(editor.KeysForSection("providers"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var providerKeys = UsageProviderDescriptorRegistry.All
            .SelectMany(descriptor => NativeSettingsEditor.KeysForProvider(descriptor.Id))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var registered = (SettingsForm)fixture.Settings.Settings.ToContent().Single();
        var registeredKeys = RegisteredInputIds(System.Text.Json.Nodes.JsonNode.Parse(registered.TemplateJson)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(registeredKeys, keys.Concat(providerKeys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Assert.Throws<ArgumentException>(() => editor.KeysForSection("unknown"));
        Assert.Throws<ArgumentException>(() => NativeSettingsEditor.KeysForProvider("unknown"));
    }

    private static IEnumerable<string> RegisteredInputIds(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>().StartsWith("Input.", StringComparison.Ordinal) == true)
            {
                yield return obj["id"]!.GetValue<string>();
                yield break;
            }
            foreach (var child in obj.Select(pair => pair.Value))
            foreach (var id in RegisteredInputIds(child))
                yield return id;
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var child in array)
            foreach (var id in RegisteredInputIds(child))
                yield return id;
        }
    }

    [Fact]
    public void FieldsExposeNativeLocalizedMetadataChoicesAndSecretMaskOnly()
    {
        using var fixture = new Fixture();
        Assert.IsType<JsonLocalizationService>(fixture.Settings.Localization).ApplyPreference("en");
        var editor = new NativeSettingsEditor(fixture.Settings);
        var fields = editor.Fields(editor.KeysForSection("general"));
        var language = Assert.Single(fields, field => field.Key == "tokensLimits.language");
        var refresh = Assert.Single(fields, field => field.Key == "tokensLimits.refreshInterval");
        var providerSecret = Assert.Single(editor.Fields(NativeSettingsEditor.KeysForProvider("deepseek")), field => field.IsSecret);

        Assert.False(string.IsNullOrWhiteSpace(language.Title));
        Assert.NotEmpty(language.Choices);
        Assert.Equal("Refresh interval", refresh.Title);
        Assert.True(refresh.IsRequired);
        Assert.Equal(string.Empty, providerSecret.Value);
        Assert.Empty(providerSecret.Placeholder);
        Assert.DoesNotContain("secret", providerSecret.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScopedSaveUsesNativeValidationAndPreservesExistingSecret()
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        var providerKeys = NativeSettingsEditor.KeysForProvider("deepseek");
        var saveErrors = editor.Save(providerKeys, new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = "test-only-secret",
            ["tokensLimits.providers.deepseek.enabled"] = "true",
        });
        Assert.Empty(saveErrors);
        Assert.Equal("test-only-secret", fixture.Settings.GetValue("deepseek", "apiKey"));
        Assert.Equal("••••••••", Assert.Single(editor.Fields(providerKeys), field => field.IsSecret).Value);

        var generalKeys = editor.KeysForSection("general");
        var errors = editor.Save(generalKeys, new Dictionary<string, string>
        {
            ["tokensLimits.language"] = "ru",
            ["tokensLimits.refreshInterval"] = "20",
        });

        Assert.Contains("tokensLimits.refreshInterval", errors.Keys);
        Assert.Equal(TimeSpan.FromSeconds(60), fixture.Settings.RefreshInterval);
        Assert.Equal("test-only-secret", fixture.Settings.GetValue("deepseek", "apiKey"));
        Assert.Throws<ArgumentException>(() => editor.Save(generalKeys, new Dictionary<string, string>
        {
            ["tokensLimits.providers.deepseek.apiKey"] = "out-of-scope",
        }));
    }

    [Fact]
    public void DisposingSettingsWithoutOpeningWindowCanBeRepeated()
    {
        using var fixture = new Fixture();

        fixture.Settings.Dispose();
        fixture.Settings.Dispose();
    }

    [Theory]
    [InlineData("replacement-test-secret")]
    [InlineData("")]
    public void SecretWriteFailureIsVisibleAndPreservesPreviousSecret(string replacement)
    {
        using var fixture = new Fixture();
        var editor = new NativeSettingsEditor(fixture.Settings);
        var keys = NativeSettingsEditor.KeysForProvider("deepseek");
        const string key = "tokensLimits.providers.deepseek.apiKey";
        Assert.Empty(editor.Save(keys, new Dictionary<string, string> { [key] = "previous-test-secret" }));
        var secretPath = Path.Combine(Path.GetDirectoryName(fixture.Settings.FilePath)!, "tokensLimits.secrets.json");
        var before = File.ReadAllBytes(secretPath);
        using (var locked = new FileStream(secretPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var errors = editor.Save(keys, new Dictionary<string, string> { [key] = replacement });
            Assert.Contains(key, errors.Keys);
            Assert.Equal("previous-test-secret", fixture.Settings.GetValue("deepseek", "apiKey"));
            Assert.Equal(TokensLimitsSettings.SecretMask, Assert.Single(editor.Fields(keys), field => field.IsSecret).Value);
            Assert.DoesNotContain(replacement.Length == 0 ? "previous-test-secret" : replacement, errors[key]);
        }
        Assert.Equal(before, File.ReadAllBytes(secretPath));
        Assert.Empty(editor.Save(keys, new Dictionary<string, string> { [key] = replacement }));
        Assert.Equal(replacement.Length == 0 ? null : replacement, fixture.Settings.GetValue("deepseek", "apiKey"));
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public TokensLimitsSettings Settings { get; }

        public Fixture() => Settings = new TokensLimitsSettings(_directory.Path);
        public void EnableAllProviders() => Settings.Settings.Update(System.Text.Json.JsonSerializer.Serialize(
            UsageProviderDescriptorRegistry.All.ToDictionary(
                descriptor => $"tokensLimits.providers.{descriptor.Id}.enabled",
                _ => "true")));
        public void Dispose() { Settings.Dispose(); _directory.Dispose(); }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TokensLimitsExtension.NativeSettingsEditor.{Guid.NewGuid():N}");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
