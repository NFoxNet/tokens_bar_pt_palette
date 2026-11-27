using System.Collections;
using System.Reflection;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Localization;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class FeatureSettingsIntegrationTests
{
    [Fact]
    public void HistoryAndNotificationsAreOptInWithConservativeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureSettings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            var registered = GetSettings(settings);
            Assert.False(Assert.IsType<ToggleSetting>(registered["tokensLimits.notifications.enabled"]).Value);
            Assert.False(Assert.IsType<ToggleSetting>(registered["tokensLimits.history.enabled"]).Value);
            Assert.Equal("10", Assert.IsType<TextSetting>(registered["tokensLimits.notifications.threshold"]).Value);
            Assert.Equal("14", Assert.IsType<ChoiceSetSetting>(registered["tokensLimits.history.retentionDays"]).Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void OptionalFeatureSettingsAreLocalized(string culture)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureSettings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            Assert.IsType<JsonLocalizationService>(settings.Localization).ApplyPreference(culture);
            foreach (var key in new[] { "tokensLimits.notifications.enabled", "tokensLimits.notifications.threshold", "tokensLimits.history.enabled", "tokensLimits.history.retentionDays" })
            {
                var registered = GetSettings(settings);
                Assert.True(registered.Contains(key));
                var setting = registered[key]!;
                var label = Assert.IsType<string>(setting.GetType().GetProperty("Label")!.GetValue(setting));
                Assert.False(string.IsNullOrWhiteSpace(label));
                Assert.DoesNotContain("settings.", label);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InvalidThresholdShowsAnErrorAndKeepsTheLastValidValue()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureSettings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            var threshold = Assert.IsType<TextSetting>(GetSettings(settings)["tokensLimits.notifications.threshold"]);
            threshold.Value = "35";
            NotifySettingsChanged(settings);
            Assert.Equal(35, settings.NotificationThresholdPercent);
            threshold.Value = "101";
            NotifySettingsChanged(settings);
            Assert.Equal(35, settings.NotificationThresholdPercent);
            Assert.False(string.IsNullOrWhiteSpace(threshold.ErrorMessage));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void FeaturePreferencesSurviveSettingsReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureSettings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var settings = new TokensLimitsSettings(directory))
            {
                var registered = GetSettings(settings);
                Assert.IsType<ToggleSetting>(registered["tokensLimits.history.enabled"]).Value = true;
                Assert.IsType<ToggleSetting>(registered["tokensLimits.notifications.enabled"]).Value = true;
                Assert.IsType<TextSetting>(registered["tokensLimits.notifications.threshold"]).Value = "25";
                Assert.IsType<ChoiceSetSetting>(registered["tokensLimits.history.retentionDays"]).Value = "7";
                settings.SaveSettings();
            }
            using var restored = new TokensLimitsSettings(directory);
            Assert.True(restored.HistoryEnabled);
            Assert.True(restored.NotificationsEnabled);
            Assert.Equal(25, restored.NotificationThresholdPercent);
            Assert.Equal(7, restored.HistoryRetentionDays);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void NotifySettingsChanged(TokensLimitsSettings settings)
        => typeof(TokensLimitsSettings).GetMethod("OnSettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(settings, [null, settings.Settings]);

    private static IDictionary GetSettings(TokensLimitsSettings settings)
        => Assert.IsAssignableFrom<IDictionary>(typeof(Microsoft.CommandPalette.Extensions.Toolkit.Settings)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings.Settings));
}
