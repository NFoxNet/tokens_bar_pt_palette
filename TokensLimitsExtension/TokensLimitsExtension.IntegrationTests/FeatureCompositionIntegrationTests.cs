using System.Collections;
using System.Reflection;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class FeatureCompositionIntegrationTests
{
    [Fact]
    public void SetupIsAccessibleFromTokensLimitsAndFromEmptyOverview()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureComposition.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            using var registry = new UsageProviderRegistry([]);
            using var provider = new TokensLimitsExtensionCommandsProvider(null, registry, settings, settingsDrivenProviders: true);
            var topLevel = Assert.Single(provider.TopLevelCommands());
            Assert.Contains(topLevel.MoreCommands!, item => item is CommandContextItem context && context.Command?.Id == "com.tokenslimits.setup");
            var setup = Assert.IsType<ProviderSetupPage>(provider.GetCommandItem("com.tokenslimits.setup")?.Command);
            Assert.Equal(UsageProviderDescriptorRegistry.All.Count, setup.GetItems().Length);
            var overview = Assert.IsType<UsageOverviewPage>(topLevel.Command);
            Assert.Contains(Assert.Single(overview.GetItems()).MoreCommands!, item => item is CommandContextItem context && context.Command?.Id == "com.tokenslimits.setup");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NotificationControllerExistsOnlyWhileEnabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TokensLimits.FeatureComposition.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TokensLimitsSettings(directory);
            using var registry = new UsageProviderRegistry([]);
            using var provider = new TokensLimitsExtensionCommandsProvider(null, registry, settings, settingsDrivenProviders: true);
            var controllerField = typeof(TokensLimitsExtensionCommandsProvider).GetField("_notificationController", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(controllerField);
            Assert.Null(controllerField.GetValue(provider));
            var registered = Assert.IsAssignableFrom<IDictionary>(typeof(Microsoft.CommandPalette.Extensions.Toolkit.Settings)
                .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings.Settings));
            var toggle = Assert.IsType<ToggleSetting>(registered["tokensLimits.notifications.enabled"]);
            toggle.Value = true;
            NotifySettingsChanged(settings);
            Assert.NotNull(controllerField.GetValue(provider));
            toggle.Value = false;
            NotifySettingsChanged(settings);
            Assert.Null(controllerField.GetValue(provider));
            Assert.False(Directory.Exists(Path.Combine(directory, "history")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void NotifySettingsChanged(TokensLimitsSettings settings)
        => typeof(TokensLimitsSettings).GetMethod("OnSettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(settings, [null, settings.Settings]);
}

