using System.Collections;
using System.Reflection;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using TokensLimitsExtension.Core.Models;
using TokensLimitsExtension.Core.Providers;
using TokensLimitsExtension.Settings;

namespace TokensLimitsExtension.IntegrationTests;

public sealed class ProviderSetupTests
{
    [Fact]
    public void AllDisabledOverviewRoutesToTheNativeExtensionSettingsPage()
    {
        using var directory = new TestDirectory();
        var settings = new TokensLimitsSettings(directory.Path);
        GetRegisteredSetting<ToggleSetting>(settings, "tokensLimits.providers.codex.enabled").Value = false;
        using var registry = new UsageProviderRegistry([new DisabledCodexProvider()]);
        var nativeSettingsPageType = settings.Settings.SettingsPage.GetType();
        using var provider = new TokensLimitsExtensionCommandsProvider(
            null,
            registry,
            settings,
            settingsDrivenProviders: true);
        var overview = Assert.IsType<UsageOverviewPage>(Assert.Single(provider.TopLevelCommands()).Command);

        var emptyState = Assert.Single(overview.GetItems());

        Assert.Equal(nativeSettingsPageType, emptyState.Command?.GetType());
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

    private sealed class DisabledCodexProvider : IUsageProvider
    {
        public UsageProviderDescriptor Descriptor => UsageProviderDescriptorRegistry.Codex;

        public Task<UsageSnapshot> GetUsageSnapshotAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A disabled provider must not be refreshed.");
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
