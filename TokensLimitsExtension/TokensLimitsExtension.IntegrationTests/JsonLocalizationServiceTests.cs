using TokensLimitsExtension.Localization;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class JsonLocalizationServiceTests
{
    [Fact]
    public void UsesParentCultureAndEnglishFallbackForMissingTranslation()
    {
        using var packaged = new TestDirectory();
        using var user = new TestDirectory();
        File.WriteAllText(Path.Combine(packaged.Path, "en.json"), """
            { "culture":"en", "nativeName":"English", "strings": { "title":"Title", "onlyEnglish":"Fallback" } }
            """);
        File.WriteAllText(Path.Combine(packaged.Path, "ru.json"), """
            { "culture":"ru", "nativeName":"Русский", "strings": { "title":"Заголовок" } }
            """);

        var localization = new JsonLocalizationService(packaged.Path, user.Path, "ru-RU");

        Assert.Equal("Заголовок", localization.GetString("title"));
        Assert.Equal("Fallback", localization.GetString("onlyEnglish"));
    }

    [Fact]
    public void UserPackOverridesPackagedStringsAndInvalidFileIsIgnored()
    {
        using var packaged = new TestDirectory();
        using var user = new TestDirectory();
        File.WriteAllText(Path.Combine(packaged.Path, "en.json"), """
            { "culture":"en", "nativeName":"English", "strings": { "title":"Title" } }
            """);
        File.WriteAllText(Path.Combine(user.Path, "en.json"), """
            { "culture":"en", "nativeName":"English", "strings": { "title":"Custom title" } }
            """);
        File.WriteAllText(Path.Combine(user.Path, "broken.json"), "not-json");
        File.WriteAllText(Path.Combine(user.Path, "bad-format.json"), """
            { "culture":"fr", "nativeName":"Français", "strings": { "title":"{0" } }
            """);

        var localization = new JsonLocalizationService(packaged.Path, user.Path, "en");

        Assert.Equal("Custom title", localization.GetString("title"));
        Assert.DoesNotContain(localization.Languages, language => language.Culture == "fr");
    }

    [Fact]
    public void PackagedEnglishAndRussianPacksSatisfyTheCompleteLanguagePackContract()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "lang");
        using var user = new TestDirectory();
        var localization = new JsonLocalizationService(packaged, user.Path, "en");

        Assert.Empty(localization.GetValidationErrors("en"));
        Assert.Empty(localization.GetValidationErrors("ru"));
    }

    [Fact]
    public void CompleteLanguagePackValidationReportsMissingKeys()
    {
        using var packaged = new TestDirectory();
        using var user = new TestDirectory();
        File.WriteAllText(Path.Combine(packaged.Path, "en.json"), """
            { "culture":"en", "nativeName":"English", "strings": { "app.title":"Title", "details.show":"{0}" } }
            """);

        var localization = new JsonLocalizationService(packaged.Path, user.Path, "en");
        var errors = localization.GetValidationErrors("en");

        Assert.Contains("Missing required key: settings.language", errors);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"TokensLimitsExtension.Localization.{Guid.NewGuid():N}");
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
